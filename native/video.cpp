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
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <algorithm>
#include <memory>
#include <cstring>
#include <thread>
#include <mutex>
#include <condition_variable>
#include <deque>
#include "alac.h"
using Microsoft::WRL::ComPtr;

struct VideoInfo { unsigned width, height, frames, hardware, presented, skipped; };
static void check(HRESULT hr) { if (FAILED(hr)) throw hr; }

class Video {
    HWND window;
    bool mfStarted = false;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11VideoDevice> videoDevice;
    ComPtr<ID3D11VideoContext> videoContext;
    ComPtr<IMFDXGIDeviceManager> manager;
    ComPtr<IMFTransform> decoder;
    ComPtr<IDXGISwapChain1> chain;
    ComPtr<ID3D11VideoProcessorEnumerator> enumerator;
    ComPtr<ID3D11VideoProcessor> processor;
    ComPtr<ID3D11Texture2D> upload;
    unsigned outputWidth = 0, outputHeight = 0, inputWidth = 0, inputHeight = 0;
    unsigned codedWidth = 0, codedHeight = 0;
    LONG stride = 0;
    RECT crop = {};
    UINT32 matrix = MFVideoTransferMatrix_BT709;
    UINT32 nominalRange = MFNominalRange_16_235;
    LONGLONG clock = 0;
    struct Frame {
        ComPtr<IMFSample> sample;
        unsigned width, height, codedWidth, codedHeight;
        LONG stride; RECT crop; UINT32 matrix, range;
    };
    std::mutex gate;
    std::condition_variable ready;
    std::deque<Frame> frames;
    std::thread presenter;
    bool stopping = false, rendering = false;
    HRESULT presentationError = S_OK;
public:
    VideoInfo info = {};
    VideoInfo snapshot() { std::lock_guard<std::mutex> lock(gate); check(presentationError); return info; }
    Video(HWND target, bool hardware) : window(target) {
        check(MFStartup(MF_VERSION, MFSTARTUP_LITE)); mfStarted = true;
        try { initialize(hardware); } catch (...) { shutdown(); throw; }
    }
    ~Video() { shutdown(); }
    void shutdown() {
        { std::lock_guard<std::mutex> lock(gate); stopping = true; frames.clear(); }
        ready.notify_all();
        if (presenter.joinable()) presenter.join();
        if (decoder) { decoder->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0); decoder.Reset(); }
        manager.Reset(); processor.Reset(); enumerator.Reset(); upload.Reset(); chain.Reset();
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
            HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            for (;;) {
                Frame frame;
                {
                    std::unique_lock<std::mutex> lock(gate);
                    ready.wait(lock, [this] { return stopping || !frames.empty(); });
                    if (stopping) break;
                    frame = std::move(frames.front()); frames.pop_front(); rendering = true;
                }
                try { render(frame); }
                catch (HRESULT error) { std::lock_guard<std::mutex> lock(gate); presentationError = error; }
                catch (...) { std::lock_guard<std::mutex> lock(gate); presentationError = E_UNEXPECTED; }
                { std::lock_guard<std::mutex> lock(gate); rendering = false; }
                ready.notify_all();
            }
            if (SUCCEEDED(apartment)) CoUninitialize();
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
    void push(const BYTE* bytes, DWORD length) {
        if (!bytes || !length || length > 8 * 1024 * 1024) throw E_INVALIDARG;
        ComPtr<IMFSample> sample; ComPtr<IMFMediaBuffer> buffer;
        check(MFCreateSample(&sample)); check(MFCreateMemoryBuffer(length, &buffer));
        BYTE* destination = nullptr;
        check(buffer->Lock(&destination, nullptr, nullptr));
        std::memcpy(destination, bytes, length); check(buffer->Unlock());
        check(buffer->SetCurrentLength(length)); check(sample->AddBuffer(buffer.Get()));
        check(sample->SetSampleTime(clock)); check(sample->SetSampleDuration(166667)); clock += 166667;
        HRESULT result = decoder->ProcessInput(0, sample.Get(), 0);
        if (result == MF_E_NOTACCEPTING) { drain(); result = decoder->ProcessInput(0, sample.Get(), 0); }
        check(result); drain();
    }
    void finish() {
        check(decoder->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0)); drain();
        std::unique_lock<std::mutex> lock(gate);
        ready.wait(lock, [this] { return frames.empty() && !rendering; });
        check(presentationError);
    }
    void drain() {
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
            {
                std::lock_guard<std::mutex> lock(gate);
                check(presentationError);
                info.hardware = hardwareFrame ? 1 : 0;
                ++info.frames;
                // Real-time queue: always keep the newest frame without latency buffer backlog.
                // Decoded frames render in <1ms on GPU, ensuring zero accumulated latency.
                if (frames.size() >= 1) { frames.pop_front(); ++info.skipped; }
                frames.push_back({frame, static_cast<unsigned>(crop.right-crop.left),
                    static_cast<unsigned>(crop.bottom-crop.top), codedWidth, codedHeight, stride, crop, matrix, nominalRange});
            }
            ready.notify_all();
        }
        throw E_UNEXPECTED;
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
            check(dxDevice->SetMaximumFrameLatency(1));
            ComPtr<IDXGIAdapter> adapter; check(dxDevice->GetAdapter(&adapter));
            ComPtr<IDXGIFactory2> factory; check(adapter->GetParent(IID_PPV_ARGS(&factory)));
            DXGI_SWAP_CHAIN_DESC1 desc = {}; desc.Width = w; desc.Height = h;
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
            desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 3;
            desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
            check(factory->CreateSwapChainForHwnd(device.Get(), window, &desc, nullptr, nullptr, &chain));
            factory->MakeWindowAssociation(window, DXGI_MWA_NO_ALT_ENTER);
        } else if (w != outputWidth || h != outputHeight) {
            context->ClearState(); context->Flush();
            check(chain->ResizeBuffers(0, w, h, DXGI_FORMAT_UNKNOWN, 0));
        }
        D3D11_TEXTURE2D_DESC source = {}; texture->GetDesc(&source);
        if (!processor || w != outputWidth || h != outputHeight || inputWidth != source.Width || inputHeight != source.Height) {
            processor.Reset(); enumerator.Reset();
            D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc = {};
            desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
            desc.InputWidth = source.Width; desc.InputHeight = source.Height;
            desc.OutputWidth = w; desc.OutputHeight = h;
            desc.InputFrameRate = {60, 1}; desc.OutputFrameRate = {60, 1};
            desc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
            check(videoDevice->CreateVideoProcessorEnumerator(&desc, &enumerator));
            check(videoDevice->CreateVideoProcessor(enumerator.Get(), 0, &processor));
            inputWidth = source.Width; inputHeight = source.Height;
        }
        outputWidth = w; outputHeight = h;
        ComPtr<ID3D11Texture2D> back; check(chain->GetBuffer(0, IID_PPV_ARGS(&back)));
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC od = {}; od.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        ComPtr<ID3D11VideoProcessorOutputView> output;
        check(videoDevice->CreateVideoProcessorOutputView(back.Get(), enumerator.Get(), &od, &output));
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
        // Direct flip presentation without VSync thread blocking: DWM handles composition
        // asynchronously at screen refresh rate while the decode loop runs unhindered.
        HRESULT result = chain->Present(0, 0);
        std::lock_guard<std::mutex> lock(gate);
        if (result == DXGI_STATUS_OCCLUDED || SUCCEEDED(result)) {
            ++info.presented;
            if (result == DXGI_STATUS_OCCLUDED) {
                // If occluded or hidden (e.g. tests or minimized), pace slightly to prevent spinning.
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
            }
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
API HRESULT na_video_create(HWND target, int hardware, void** result) noexcept {
    if (!result || !IsWindow(target)) return E_INVALIDARG; *result = nullptr;
    Apartment apartment;
    try { *result = new Video(target, hardware != 0); return S_OK; }
    catch (HRESULT hr) { return hr; } catch (...) { return E_OUTOFMEMORY; }
}
API HRESULT na_video_push(void* handle, const BYTE* bytes, DWORD length, VideoInfo* info) noexcept {
    if (!handle || !info) return E_INVALIDARG; Apartment apartment;
    try { auto video = static_cast<Video*>(handle); video->push(bytes, length); *info = video->snapshot(); return S_OK; }
    catch (HRESULT hr) { return hr; } catch (...) { return E_UNEXPECTED; }
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
