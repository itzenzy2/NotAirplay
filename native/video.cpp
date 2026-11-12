// Original NotAirplay Windows video backend. Compressed samples stay in-process;
// DXVA output goes directly through the D3D11 video processor to the swap chain.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mftransform.h>
#include <wmcodecdsp.h>
#include <codecapi.h>
#include <d3d11.h>
#include <d3d10.h>
#include <dxgi1_3.h>
#include <wrl/client.h>
#include <algorithm>
#include <chrono>
#include <cstdint>
#include <memory>
#include <cstring>
#include <thread>
#include <mutex>
#include <condition_variable>
#include <deque>
#include <vector>
#include "alac.h"
using Microsoft::WRL::ComPtr;

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

struct VideoInfo { unsigned width, height, frames, hardware, presented, skipped; };
static void check(HRESULT hr) { if (FAILED(hr)) throw hr; }

static uint64_t nowMicros() noexcept {
    static LARGE_INTEGER freq = [] { LARGE_INTEGER f = {}; QueryPerformanceFrequency(&f); return f; }();
    LARGE_INTEGER counter = {};
    QueryPerformanceCounter(&counter);
    return static_cast<uint64_t>((counter.QuadPart * 1000000LL) / freq.QuadPart);
}

struct MultimediaGuard {
    HMODULE winmm = nullptr;
    UINT periodMs = 0;
    using TimePeriodFn = UINT(WINAPI*)(UINT);
    void acquire() noexcept {
        if (periodMs) return;
        winmm = LoadLibraryW(L"winmm.dll");
        if (!winmm) return;
        auto beginFn = reinterpret_cast<TimePeriodFn>(reinterpret_cast<void*>(GetProcAddress(winmm, "timeBeginPeriod")));
        if (beginFn && beginFn(1) == 0) periodMs = 1;
    }
    void release() noexcept {
        if (winmm && periodMs) {
            auto endFn = reinterpret_cast<TimePeriodFn>(reinterpret_cast<void*>(GetProcAddress(winmm, "timeEndPeriod")));
            if (endFn) endFn(periodMs);
            periodMs = 0;
        }
        if (winmm) { FreeLibrary(winmm); winmm = nullptr; }
    }
};

class Video {
    HWND window;
    bool mfStarted = false;
    MultimediaGuard mmTimer;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11VideoDevice> videoDevice;
    ComPtr<ID3D11VideoContext> videoContext;
    ComPtr<IMFDXGIDeviceManager> manager;
    ComPtr<IMFTransform> decoder;
    ComPtr<IDXGISwapChain1> chain;
    ComPtr<IDXGISwapChain2> chain2;
    ComPtr<ID3D11VideoProcessorEnumerator> enumerator;
    ComPtr<ID3D11VideoProcessor> processor;
    ComPtr<ID3D11Texture2D> upload;
    struct OutputViewEntry {
        ID3D11Texture2D* key = nullptr;
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<ID3D11VideoProcessorOutputView> view;
    };
    std::vector<OutputViewEntry> outputViews;
    HANDLE waitable = nullptr;
    HANDLE stopEvent = nullptr;
    HANDLE catchupEvent = nullptr;
    HANDLE waitTimer = nullptr;
    UINT swapFlags = 0;
    bool lastOccluded = false;
    unsigned targetFps = 60;
    unsigned outputWidth = 0, outputHeight = 0, inputWidth = 0, inputHeight = 0;
    unsigned codedWidth = 0, codedHeight = 0;
    LONG stride = 0;
    RECT crop = {};
    UINT32 matrix = MFVideoTransferMatrix_BT709;
    UINT32 nominalRange = MFNominalRange_16_235;
    LONGLONG clock = 0;
    struct Frame {
        ComPtr<IMFSample> sample;
        uint64_t senderTsUs;
        unsigned width, height, codedWidth, codedHeight;
        LONG stride; RECT crop; UINT32 matrix, range;
    };
    std::mutex gate;
    std::condition_variable ready;
    std::deque<Frame> frames;
    std::thread presenter;
    bool stopping = false, rendering = false;
    HRESULT presentationError = S_OK;

    bool pacingInit = false;
    uint64_t lastSenderUs = 0;
    int64_t clockOffsetUs = 0;
    double jitterUs = 0.0;
public:
    VideoInfo info = {};
    static unsigned normalizeFps(unsigned fps) noexcept {
        return (fps == 30 || fps == 120) ? fps : 60u;
    }
    VideoInfo snapshot() { std::lock_guard<std::mutex> lock(gate); check(presentationError); return info; }
    Video(HWND target, bool hardware, unsigned fps = 60) : window(target), targetFps(normalizeFps(fps)) {
        check(MFStartup(MF_VERSION, MFSTARTUP_LITE)); mfStarted = true;
        stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        catchupEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        waitTimer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (!waitTimer) waitTimer = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
        mmTimer.acquire();
        try { initialize(hardware); } catch (...) { shutdown(); throw; }
    }
    ~Video() { shutdown(); }
    void shutdown() {
        { std::lock_guard<std::mutex> lock(gate); stopping = true; frames.clear(); }
        if (stopEvent) SetEvent(stopEvent);
        ready.notify_all();
        if (presenter.joinable()) presenter.join();
        if (decoder) { decoder->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0); decoder.Reset(); }
        outputViews.clear();
        manager.Reset(); processor.Reset(); enumerator.Reset(); upload.Reset(); chain2.Reset(); chain.Reset();
        if (waitable) { CloseHandle(waitable); waitable = nullptr; }
        if (waitTimer) { CloseHandle(waitTimer); waitTimer = nullptr; }
        if (catchupEvent) { CloseHandle(catchupEvent); catchupEvent = nullptr; }
        if (stopEvent) { CloseHandle(stopEvent); stopEvent = nullptr; }
        mmTimer.release();
        videoContext.Reset(); videoDevice.Reset(); context.Reset(); device.Reset();
        if (mfStarted) { MFShutdown(); mfStarted = false; }
    }
    void initialize(bool hardware) {
        check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context));
        ComPtr<ID3D10Multithread> multithread;
        check(device.As(&multithread)); multithread->SetMultithreadProtected(TRUE);
        check(device.As(&videoDevice)); check(context.As(&videoContext));
        check(CoCreateInstance(CLSID_CMSH264DecoderMFT, nullptr, CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(&decoder)));
        ComPtr<IMFAttributes> attributes;
        check(decoder->GetAttributes(&attributes));
        attributes->SetUINT32(CODECAPI_AVLowLatencyMode, TRUE);
        attributes->SetUINT32(CODECAPI_AVDecNumWorkerThreads, 2);
        static const GUID MF_LOW_LATENCY_GUID = { 0x9c27891a, 0xed7a, 0x40e1, { 0x88, 0xe8, 0xb2, 0x27, 0x27, 0xa0, 0x24, 0xee } };
        attributes->SetUINT32(MF_LOW_LATENCY_GUID, TRUE);
        UINT32 aware = 0;
        if (hardware && SUCCEEDED(attributes->GetUINT32(MF_SA_D3D11_AWARE, &aware)) && aware) {
            UINT reset = 0;
            check(MFCreateDXGIDeviceManager(&reset, &manager));
            check(manager->ResetDevice(device.Get(), reset));
            check(decoder->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(manager.Get())));
        }
        ComPtr<IMFMediaType> type;
        check(MFCreateMediaType(&type));
        check(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        check(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264));
        check(decoder->SetInputType(0, type.Get(), 0));
        setOutput();
        check(decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0));
        check(decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0));
        presenter = std::thread([this] {
            SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
            HMODULE avrt = LoadLibraryW(L"avrt.dll");
            HANDLE mmcss = nullptr;
            if (avrt) {
                using AvSetFn = HANDLE(WINAPI*)(LPCWSTR, LPDWORD);
                auto setFn = reinterpret_cast<AvSetFn>(reinterpret_cast<void*>(GetProcAddress(avrt, "AvSetMmThreadCharacteristicsW")));
                DWORD taskIndex = 0;
                if (setFn) mmcss = setFn(L"Games", &taskIndex);
            }
            HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            for (;;) {
                Frame frame;
                size_t remaining = 0;
                {
                    std::unique_lock<std::mutex> lock(gate);
                    ready.wait(lock, [this] { return stopping || !frames.empty(); });
                    if (stopping) break;
                    frame = std::move(frames.front()); frames.pop_front();
                    remaining = frames.size();
                    rendering = true;
                }
                try {
                    paceFrame(frame.senderTsUs, remaining);
                    if (!stopping) render(frame);
                }
                catch (HRESULT error) { std::lock_guard<std::mutex> lock(gate); presentationError = error; }
                catch (...) { std::lock_guard<std::mutex> lock(gate); presentationError = E_UNEXPECTED; }
                { std::lock_guard<std::mutex> lock(gate); rendering = false; }
                ready.notify_all();
            }
            if (SUCCEEDED(apartment)) CoUninitialize();
            if (avrt) {
                if (mmcss) {
                    using AvRevertFn = BOOL(WINAPI*)(HANDLE);
                    auto revertFn = reinterpret_cast<AvRevertFn>(reinterpret_cast<void*>(GetProcAddress(avrt, "AvRevertMmThreadCharacteristics")));
                    if (revertFn) revertFn(mmcss);
                }
                FreeLibrary(avrt);
            }
        });
    }
    void setOutput() {
        for (DWORD i = 0; ; ++i) {
            ComPtr<IMFMediaType> type;
            check(decoder->GetOutputAvailableType(0, i, &type));
            GUID subtype = {};
            check(type->GetGUID(MF_MT_SUBTYPE, &subtype));
            if (subtype != MFVideoFormat_NV12) continue;
            check(decoder->SetOutputType(0, type.Get(), 0));
            check(MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &codedWidth, &codedHeight));
            if (!codedWidth || !codedHeight || codedWidth > 4096 || codedHeight > 4096) throw E_INVALIDARG;
            crop = {0, 0, static_cast<LONG>(codedWidth), static_cast<LONG>(codedHeight)};
            MFVideoArea area = {}; UINT32 actual = 0;
            if (SUCCEEDED(type->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE,
                    reinterpret_cast<UINT8*>(&area), sizeof(area), &actual)) && actual == sizeof(area) &&
                area.Area.cx > 0 && area.Area.cy > 0 && area.OffsetX.value >= 0 && area.OffsetY.value >= 0 &&
                area.OffsetX.value + area.Area.cx <= static_cast<LONG>(codedWidth) &&
                area.OffsetY.value + area.Area.cy <= static_cast<LONG>(codedHeight)) {
                crop = {area.OffsetX.value, area.OffsetY.value,
                    area.OffsetX.value + area.Area.cx, area.OffsetY.value + area.Area.cy};
            }
            { std::lock_guard<std::mutex> lock(gate); info.width = crop.right - crop.left; info.height = crop.bottom - crop.top; }
            matrix = (crop.bottom - crop.top) > 576 ? MFVideoTransferMatrix_BT709 : MFVideoTransferMatrix_BT601;
            nominalRange = MFNominalRange_16_235;
            type->GetUINT32(MF_MT_YUV_MATRIX, &matrix);
            type->GetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, &nominalRange);
            UINT32 rawStride = codedWidth;
            type->GetUINT32(MF_MT_DEFAULT_STRIDE, &rawStride); stride = static_cast<LONG>(rawStride);
            return;
        }
    }
    void push(const BYTE* bytes, DWORD length, uint64_t senderTsUs = 0) {
        if (!bytes || !length || length > 8 * 1024 * 1024) throw E_INVALIDARG;
        ComPtr<IMFSample> sample; ComPtr<IMFMediaBuffer> buffer;
        check(MFCreateSample(&sample)); check(MFCreateMemoryBuffer(length, &buffer));
        BYTE* destination = nullptr;
        check(buffer->Lock(&destination, nullptr, nullptr));
        std::memcpy(destination, bytes, length); check(buffer->Unlock());
        check(buffer->SetCurrentLength(length)); check(sample->AddBuffer(buffer.Get()));
        LONGLONG duration = 10000000LL / static_cast<LONGLONG>(targetFps);
        check(sample->SetSampleTime(clock)); check(sample->SetSampleDuration(duration)); clock += duration;
        HRESULT result = decoder->ProcessInput(0, sample.Get(), 0);
        if (result == MF_E_NOTACCEPTING) { drain(0); result = decoder->ProcessInput(0, sample.Get(), 0); }
        check(result); drain(senderTsUs);
    }
    void finish() {
        check(decoder->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0)); drain(0);
        std::unique_lock<std::mutex> lock(gate);
        ready.wait(lock, [this] { return frames.empty() && !rendering; });
        check(presentationError);
    }
    void drain(uint64_t senderTsUs) {
        constexpr size_t kMaxQueue = 3;
        bool firstOut = true;
        for (unsigned safety = 0; safety < 64; ++safety) {
            MFT_OUTPUT_STREAM_INFO outputInfo = {}; check(decoder->GetOutputStreamInfo(0, &outputInfo));
            ComPtr<IMFSample> ownSample;
            if (!(outputInfo.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)) {
                check(MFCreateSample(&ownSample)); ComPtr<IMFMediaBuffer> buffer;
                check(MFCreateAlignedMemoryBuffer(outputInfo.cbSize, outputInfo.cbAlignment ? outputInfo.cbAlignment - 1 : 0, &buffer));
                check(ownSample->AddBuffer(buffer.Get()));
            }
            MFT_OUTPUT_DATA_BUFFER output = {}; output.pSample = ownSample.Get(); DWORD status = 0;
            HRESULT result = decoder->ProcessOutput(0, 1, &output, &status);
            if (output.pEvents) output.pEvents->Release();
            ComPtr<IMFSample> frame;
            if (output.pSample && output.pSample != ownSample.Get()) frame.Attach(output.pSample);
            else frame = ownSample;
            if (result == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
            if (result == MF_E_TRANSFORM_STREAM_CHANGE) { setOutput(); continue; }
            check(result);
            if (!frame || (output.dwStatus & MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE)) continue;
            ComPtr<IMFMediaBuffer> decodedBuffer; ComPtr<IMFDXGIBuffer> decodedTexture;
            bool hardwareFrame = SUCCEEDED(frame->GetBufferByIndex(0, &decodedBuffer)) && SUCCEEDED(decodedBuffer.As(&decodedTexture));
            uint64_t ts = firstOut ? senderTsUs : 0;
            firstOut = false;
            bool signalCatchup = false;
            {
                std::lock_guard<std::mutex> lock(gate);
                check(presentationError);
                info.hardware = hardwareFrame ? 1 : 0;
                ++info.frames;
                while (frames.size() >= kMaxQueue) { frames.pop_front(); ++info.skipped; }
                frames.push_back({frame, ts, static_cast<unsigned>(crop.right-crop.left),
                    static_cast<unsigned>(crop.bottom-crop.top), codedWidth, codedHeight, stride, crop, matrix, nominalRange});
                signalCatchup = frames.size() >= 2;
            }
            if (signalCatchup && catchupEvent) SetEvent(catchupEvent);
            ready.notify_all();
        }
        throw E_UNEXPECTED;
    }
    void paceFrame(uint64_t senderTsUs, size_t remaining) {
        if (senderTsUs == 0 || remaining >= 2) return;
        uint64_t now = nowMicros();
        if (!pacingInit || senderTsUs <= lastSenderUs || (senderTsUs - lastSenderUs) > 250000ULL) {
            clockOffsetUs = static_cast<int64_t>(now) - static_cast<int64_t>(senderTsUs);
            jitterUs = 0.0;
            lastSenderUs = senderTsUs;
            pacingInit = true;
            return; // Zero idle-to-touch delay on the first frame after a pause.
        }
        lastSenderUs = senderTsUs;
        int64_t sampleOffset = static_cast<int64_t>(now) - static_cast<int64_t>(senderTsUs);
        if (sampleOffset < clockOffsetUs) {
            clockOffsetUs = sampleOffset;
        } else {
            clockOffsetUs += (sampleOffset - clockOffsetUs) / 256;
        }
        int64_t lateness = sampleOffset - clockOffsetUs;
        if (lateness < 0) lateness = 0;
        if (lateness > 20000) lateness = 20000;
        jitterUs = std::clamp(jitterUs * 0.90 + static_cast<double>(lateness) * 0.10, 0.0, 14000.0);
        int64_t margin = static_cast<int64_t>(std::clamp(jitterUs * 1.25, 0.0, 14000.0));
        int64_t targetUs = static_cast<int64_t>(senderTsUs) + clockOffsetUs + margin;
        int64_t waitUs = targetUs - static_cast<int64_t>(now);
        if (waitUs <= 500 || !waitTimer) return;
        if (waitUs > 20000) waitUs = 20000;

        if (catchupEvent) ResetEvent(catchupEvent);
        {
            std::lock_guard<std::mutex> lock(gate);
            if (stopping || frames.size() >= 2) return;
        }
        LARGE_INTEGER due = {};
        due.QuadPart = -static_cast<LONGLONG>(waitUs * 10);
        if (SetWaitableTimer(waitTimer, &due, 0, nullptr, nullptr, FALSE)) {
            HANDLE handles[3] = { stopEvent, catchupEvent, waitTimer };
            WaitForMultipleObjects(3, handles, FALSE, 25);
        }
    }
    void render(const Frame& frame) {
        auto sample = frame.sample.Get();
        auto codedWidth = frame.codedWidth, codedHeight = frame.codedHeight;
        auto stride = frame.stride; auto crop = frame.crop;
        auto matrix = frame.matrix, nominalRange = frame.range;
        ComPtr<IMFMediaBuffer> buffer; check(sample->GetBufferByIndex(0, &buffer));
        ComPtr<IMFDXGIBuffer> dxgi; ComPtr<ID3D11Texture2D> texture; UINT subresource = 0;
        if (SUCCEEDED(buffer.As(&dxgi))) {
            check(dxgi->GetResource(IID_PPV_ARGS(&texture))); check(dxgi->GetSubresourceIndex(&subresource));
            { std::lock_guard<std::mutex> lock(gate); info.hardware = 1; }
        } else {
            { std::lock_guard<std::mutex> lock(gate); info.hardware = 0; }
            if (upload) { D3D11_TEXTURE2D_DESC current = {}; upload->GetDesc(&current);
                if (current.Width != codedWidth || current.Height != codedHeight) upload.Reset(); }
            if (!upload) {
                D3D11_TEXTURE2D_DESC desc = {}; desc.Width = codedWidth; desc.Height = codedHeight;
                desc.MipLevels = 1; desc.ArraySize = 1; desc.Format = DXGI_FORMAT_NV12;
                desc.SampleDesc.Count = 1; desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_DECODER;
                check(device->CreateTexture2D(&desc, nullptr, &upload));
            }
            ComPtr<IMF2DBuffer> twoD;
            BYTE* data = nullptr; LONG pitch = stride;
            if (SUCCEEDED(buffer.As(&twoD))) {
                check(twoD->Lock2D(&data, &pitch));
                if (pitch > 0) context->UpdateSubresource(upload.Get(), 0, nullptr, data, pitch, 0);
                twoD->Unlock2D(); if (pitch <= 0) throw E_UNEXPECTED;
            } else {
                DWORD size = 0; check(buffer->Lock(&data, nullptr, &size));
                bool valid = pitch > 0 && size >= static_cast<UINT64>(pitch) * codedHeight * 3 / 2;
                if (valid) context->UpdateSubresource(upload.Get(), 0, nullptr, data, pitch, 0);
                buffer->Unlock(); if (!valid) throw E_UNEXPECTED;
            }
            texture = upload;
        }
        RECT rect = {}; check(GetClientRect(window, &rect) ? S_OK : E_HANDLE);
        unsigned w = rect.right, h = rect.bottom;
        if (!w || !h) { std::lock_guard<std::mutex> lock(gate); ++info.skipped; return; }
        w = std::min(w, 8192u); h = std::min(h, 8192u);
        if (!chain) {
            ComPtr<IDXGIDevice1> dxDevice; check(device.As(&dxDevice));
            ComPtr<IDXGIAdapter> adapter; check(dxDevice->GetAdapter(&adapter));
            ComPtr<IDXGIFactory2> factory; check(adapter->GetParent(IID_PPV_ARGS(&factory)));
            DXGI_SWAP_CHAIN_DESC1 desc = {}; desc.Width = w; desc.Height = h;
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
            desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 3;
            desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
            desc.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
            HRESULT hrChain = factory->CreateSwapChainForHwnd(device.Get(), window, &desc, nullptr, nullptr, &chain);
            if (SUCCEEDED(hrChain)) {
                swapFlags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
                if (SUCCEEDED(chain.As(&chain2))) {
                    check(chain2->SetMaximumFrameLatency(1));
                    waitable = chain2->GetFrameLatencyWaitableObject();
                }
            } else {
                desc.Flags = 0;
                swapFlags = 0;
                check(dxDevice->SetMaximumFrameLatency(1));
                check(factory->CreateSwapChainForHwnd(device.Get(), window, &desc, nullptr, nullptr, &chain));
            }
            factory->MakeWindowAssociation(window, DXGI_MWA_NO_ALT_ENTER);
        } else if (w != outputWidth || h != outputHeight) {
            outputViews.clear();
            context->ClearState(); context->Flush();
            check(chain->ResizeBuffers(0, w, h, DXGI_FORMAT_UNKNOWN, swapFlags));
        }
        D3D11_TEXTURE2D_DESC source = {}; texture->GetDesc(&source);
        if (!processor || w != outputWidth || h != outputHeight || inputWidth != source.Width || inputHeight != source.Height) {
            outputViews.clear();
            processor.Reset(); enumerator.Reset();
            D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc = {};
            desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
            desc.InputWidth = source.Width; desc.InputHeight = source.Height;
            desc.OutputWidth = w; desc.OutputHeight = h;
            desc.InputFrameRate = {targetFps, 1}; desc.OutputFrameRate = {targetFps, 1};
            desc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
            check(videoDevice->CreateVideoProcessorEnumerator(&desc, &enumerator));
            check(videoDevice->CreateVideoProcessor(enumerator.Get(), 0, &processor));
            inputWidth = source.Width; inputHeight = source.Height;
        }
        outputWidth = w; outputHeight = h;
        bool visible = IsWindowVisible(window) != FALSE;
        if (waitable && visible && !lastOccluded && stopEvent) {
            HANDLE handles[2] = { stopEvent, waitable };
            if (WaitForMultipleObjects(2, handles, FALSE, 50) == WAIT_OBJECT_0) return;
        }
        ComPtr<ID3D11Texture2D> back; check(chain->GetBuffer(0, IID_PPV_ARGS(&back)));
        ComPtr<ID3D11VideoProcessorOutputView> output;
        for (const auto& entry : outputViews) {
            if (entry.key == back.Get()) { output = entry.view; break; }
        }
        if (!output) {
            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC od = {}; od.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
            check(videoDevice->CreateVideoProcessorOutputView(back.Get(), enumerator.Get(), &od, &output));
            outputViews.push_back({ back.Get(), back, output });
        }
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC id = {}; id.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        id.Texture2D.ArraySlice = subresource / source.MipLevels; id.Texture2D.MipSlice = subresource % source.MipLevels;
        ComPtr<ID3D11VideoProcessorInputView> input;
        check(videoDevice->CreateVideoProcessorInputView(texture.Get(), enumerator.Get(), &id, &input));
        double scale = std::min(static_cast<double>(w) / frame.width, static_cast<double>(h) / frame.height);
        LONG dw = static_cast<LONG>(frame.width * scale + .5), dh = static_cast<LONG>(frame.height * scale + .5);
        RECT dest = {(static_cast<LONG>(w) - dw) / 2, (static_cast<LONG>(h) - dh) / 2, 0, 0};
        dest.right = dest.left + dw; dest.bottom = dest.top + dh;
        videoContext->VideoProcessorSetStreamSourceRect(processor.Get(), 0, TRUE, &crop);
        videoContext->VideoProcessorSetStreamDestRect(processor.Get(), 0, TRUE, &dest);
        videoContext->VideoProcessorSetOutputTargetRect(processor.Get(), TRUE, &rect);
        D3D11_VIDEO_COLOR black = {}; black.RGBA.A = 1;
        videoContext->VideoProcessorSetOutputBackgroundColor(processor.Get(), FALSE, &black);
        videoContext->VideoProcessorSetStreamAutoProcessingMode(processor.Get(), 0, FALSE);
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE inputSpace = {}, outputSpace = {};
        inputSpace.YCbCr_Matrix = matrix == MFVideoTransferMatrix_BT601 ? 0 : 1;
        inputSpace.Nominal_Range = nominalRange == MFNominalRange_0_255
            ? D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_0_255 : D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
        outputSpace.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_0_255;
        videoContext->VideoProcessorSetStreamColorSpace(processor.Get(), 0, &inputSpace);
        videoContext->VideoProcessorSetOutputColorSpace(processor.Get(), &outputSpace);
        D3D11_VIDEO_PROCESSOR_STREAM stream = {}; stream.Enable = TRUE; stream.pInputSurface = input.Get();
        check(videoContext->VideoProcessorBlt(processor.Get(), output.Get(), 0, 1, &stream));
        // VSync-locked presentation via Waitable Swap Chain eliminates mid-refresh tearing
        // and aligns flips cleanly to display refresh without deep buffer bloat.
        UINT syncInterval = (visible && !lastOccluded) ? 1u : 0u;
        HRESULT result = chain->Present(syncInterval, 0);
        lastOccluded = (result == DXGI_STATUS_OCCLUDED);
        if (lastOccluded && stopEvent) {
            WaitForSingleObject(stopEvent, 5);
        }
        std::lock_guard<std::mutex> lock(gate);
        if (result == DXGI_STATUS_OCCLUDED || SUCCEEDED(result)) {
            ++info.presented;
        } else { check(result); }
    }
};

// COM initialization is scoped to each entry point. Calls for one Video are serialized
// on the managed decoder worker; no UI-thread calls touch its immediate context.
struct Apartment {
    HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ~Apartment() { if (SUCCEEDED(result)) CoUninitialize(); }
};
#define API extern "C" __declspec(dllexport)
API HRESULT na_video_create_with_fps(HWND target, int hardware, unsigned targetFps, void** result) noexcept {
    if (!result || !IsWindow(target)) return E_INVALIDARG; *result = nullptr;
    Apartment apartment;
    try { *result = new Video(target, hardware != 0, targetFps); return S_OK; }
    catch (HRESULT hr) { return hr; } catch (...) { return E_OUTOFMEMORY; }
}
API HRESULT na_video_create(HWND target, int hardware, void** result) noexcept {
    return na_video_create_with_fps(target, hardware, 60, result);
}
API HRESULT na_video_push_timed(void* handle, const BYTE* bytes, DWORD length, uint64_t timestampUs, VideoInfo* info) noexcept {
    if (!handle || !info) return E_INVALIDARG; Apartment apartment;
    try { auto video = static_cast<Video*>(handle); video->push(bytes, length, timestampUs); *info = video->snapshot(); return S_OK; }
    catch (HRESULT hr) { return hr; } catch (...) { return E_UNEXPECTED; }
}
API HRESULT na_video_push(void* handle, const BYTE* bytes, DWORD length, VideoInfo* info) noexcept {
    return na_video_push_timed(handle, bytes, length, 0, info);
}
API HRESULT na_video_finish(void* handle, VideoInfo* info) noexcept {
    if (!handle || !info) return E_INVALIDARG; Apartment apartment;
    try { auto video = static_cast<Video*>(handle); video->finish(); *info = video->snapshot(); return S_OK; }
    catch (HRESULT hr) { return hr; } catch (...) { return E_UNEXPECTED; }
}
API void na_video_destroy(void* handle) noexcept { Apartment apartment; delete static_cast<Video*>(handle); }

API void* na_alac_create(int sample_size, int num_channels, int frame_size) noexcept {
    auto alac = alac_create(sample_size, num_channels);
    if (!alac) return nullptr;
    alac->setinfo_max_samples_per_frame = frame_size > 0 ? frame_size : 352;
    alac->setinfo_7a = 0;
    alac->setinfo_sample_size = static_cast<uint8_t>(sample_size);
    alac->setinfo_rice_historymult = 40;
    alac->setinfo_rice_initialhistory = 10;
    alac->setinfo_rice_kmodifier = 14;
    alac->setinfo_7f = static_cast<uint8_t>(num_channels);
    alac->setinfo_80 = 255;
    alac->setinfo_82 = 0;
    alac->setinfo_86 = 0;
    alac->setinfo_8a_rate = 44100;
    alac_allocate_buffers(alac);
    return alac;
}

API int na_alac_decode(void* handle, const unsigned char* in_buffer, int in_bytes, unsigned char* out_buffer, int max_out_bytes) noexcept {
    if (!handle || !in_buffer || in_bytes <= 0 || !out_buffer || max_out_bytes <= 0) return 0;
    auto alac = static_cast<alac_file*>(handle);
    int outsize = 0;
    try {
        alac_decode_frame(alac, const_cast<unsigned char*>(in_buffer), out_buffer, &outsize);
        if (outsize < 0 || outsize > max_out_bytes) return 0;
        return outsize;
    } catch (...) {
        return 0;
    }
}

API void na_alac_destroy(void* handle) noexcept {
    if (handle) alac_free(static_cast<alac_file*>(handle));
}
