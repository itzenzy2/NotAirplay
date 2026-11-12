// In-process Windows WASAPI audio renderer and Media Foundation AAC-LC decoder.
#define WIN32_LEAN_AND_MEAN
#define INITGUID
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <audiopolicy.h>
#include <functiondiscoverykeys_devpkey.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mftransform.h>
#include <wmcodecdsp.h>
#include <wrl/client.h>

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <future>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {

constexpr UINT32 kSampleRate = 44100;
constexpr UINT32 kChannels = 2;
constexpr UINT32 kBlockAlign = kChannels * 2; // 16-bit stereo = 4 bytes/frame
constexpr size_t kRingFrames = 22050;         // 500 ms ring buffer
constexpr size_t kPrerollFrames = 1764;       // 40 ms initial pre-roll
constexpr size_t kRecoverPrerollFrames = 1058; // 24 ms recovery pre-roll
constexpr size_t kHighWaterFrames = 8820;     // 200 ms max lag watermark
constexpr size_t kTargetTrimFrames = 2646;    // 60 ms target after burst trim
constexpr size_t kUnderrunFadeSamples = 32;

struct ComScope {
    HRESULT hrCom = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ~ComScope() { if (SUCCEEDED(hrCom)) CoUninitialize(); }
};

struct MfApartment {
    HRESULT hrCom = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    bool mfStarted = SUCCEEDED(MFStartup(MF_VERSION, MFSTARTUP_LITE));
    ~MfApartment() {
        if (mfStarted) MFShutdown();
        if (SUCCEEDED(hrCom)) CoUninitialize();
    }
};

class MfaacDecoder {
    ComPtr<IMFTransform> decoder;
    DWORD outBufSize = 49152;

public:
    bool initialize() noexcept {
        decoder.Reset();
        ComPtr<IMFTransform> dec;
        if (FAILED(CoCreateInstance(CLSID_CMSAACDecMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&dec))))
            return false;
        ComPtr<IMFMediaType> in;
        if (FAILED(MFCreateMediaType(&in))) return false;
        in->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        in->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
        in->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, kSampleRate);
        in->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, kChannels);
        in->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0);
        // HEAACWAVEINFO (12 bytes) + 2-byte AudioSpecificConfig (0x12 0x10 = AAC-LC 44.1 kHz stereo)
        BYTE userData[14] = {};
        userData[12] = 0x12;
        userData[13] = 0x10;
        in->SetBlob(MF_MT_USER_DATA, userData, sizeof(userData));
        if (FAILED(dec->SetInputType(0, in.Get(), 0))) return false;

        bool outOk = false;
        for (DWORD i = 0; ; ++i) {
            ComPtr<IMFMediaType> out;
            if (FAILED(dec->GetOutputAvailableType(0, i, &out))) break;
            GUID sub = {};
            UINT32 bps = 0, sr = 0, ch = 0;
            out->GetGUID(MF_MT_SUBTYPE, &sub);
            out->GetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, &bps);
            out->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &sr);
            out->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &ch);
            if (sub == MFAudioFormat_PCM && bps == 16 && sr == kSampleRate && ch == kChannels) {
                outOk = SUCCEEDED(dec->SetOutputType(0, out.Get(), 0));
                break;
            }
        }
        if (!outOk) return false;

        MFT_OUTPUT_STREAM_INFO osi = {};
        if (SUCCEEDED(dec->GetOutputStreamInfo(0, &osi)) && osi.cbSize > outBufSize)
            outBufSize = osi.cbSize;
        if (FAILED(dec->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0))) return false;
        if (FAILED(dec->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0))) return false;
        decoder = std::move(dec);
        return true;
    }

    void flush() noexcept {
        if (decoder) {
            decoder->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
            decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
        }
    }

    void reset() noexcept { decoder.Reset(); }

    template <typename Callback>
    HRESULT decode(const uint8_t* bytes, uint32_t length, Callback&& onPcm) noexcept {
        if (!decoder || !bytes || length == 0) return E_INVALIDARG;
        ComPtr<IMFSample> sample;
        ComPtr<IMFMediaBuffer> buf;
        HRESULT hr = MFCreateSample(&sample);
        if (FAILED(hr)) return hr;
        hr = MFCreateMemoryBuffer(length, &buf);
        if (FAILED(hr)) return hr;
        BYTE* dst = nullptr;
        hr = buf->Lock(&dst, nullptr, nullptr);
        if (FAILED(hr)) return hr;
        std::memcpy(dst, bytes, length);
        buf->Unlock();
        buf->SetCurrentLength(length);
        sample->AddBuffer(buf.Get());

        hr = decoder->ProcessInput(0, sample.Get(), 0);
        if (hr == MF_E_NOTACCEPTING) {
            drain(onPcm);
            hr = decoder->ProcessInput(0, sample.Get(), 0);
        }
        if (FAILED(hr)) return hr;
        return drain(onPcm);
    }

private:
    template <typename Callback>
    HRESULT drain(Callback&& onPcm) noexcept {
        for (int guard = 0; guard < 16; ++guard) {
            ComPtr<IMFSample> outSample;
            ComPtr<IMFMediaBuffer> outBuf;
            HRESULT hr = MFCreateSample(&outSample);
            if (FAILED(hr)) return hr;
            hr = MFCreateMemoryBuffer(outBufSize, &outBuf);
            if (FAILED(hr)) return hr;
            outSample->AddBuffer(outBuf.Get());

            MFT_OUTPUT_DATA_BUFFER odb = {};
            odb.pSample = outSample.Get();
            DWORD status = 0;
            hr = decoder->ProcessOutput(0, 1, &odb, &status);
            if (odb.pEvents) odb.pEvents->Release();
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return S_OK;
            if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
                for (DWORD i = 0; ; ++i) {
                    ComPtr<IMFMediaType> out;
                    if (FAILED(decoder->GetOutputAvailableType(0, i, &out))) break;
                    GUID sub = {};
                    UINT32 bps = 0;
                    out->GetGUID(MF_MT_SUBTYPE, &sub);
                    out->GetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, &bps);
                    if (sub == MFAudioFormat_PCM && bps == 16) {
                        decoder->SetOutputType(0, out.Get(), 0);
                        break;
                    }
                }
                continue;
            }
            if (FAILED(hr)) return hr;

            ComPtr<IMFMediaBuffer> contig;
            if (odb.pSample && SUCCEEDED(odb.pSample->ConvertToContiguousBuffer(&contig))) {
                BYTE* ptr = nullptr;
                DWORD cb = 0;
                if (SUCCEEDED(contig->Lock(&ptr, nullptr, &cb))) {
                    if (cb >= kBlockAlign) onPcm(ptr, cb);
                    contig->Unlock();
                }
            }
        }
        return S_OK;
    }
};

class AudioEngine {
    std::mutex ringGate;
    std::vector<int16_t> ring;
    size_t head = 0;
    size_t tail = 0;
    size_t count = 0;
    bool prerolling = true;
    size_t prerollGoal = kPrerollFrames;
    int16_t lastSampleL = 0;
    int16_t lastSampleR = 0;

    std::mutex deviceGate;
    std::wstring targetDeviceId;
    bool rebindRequested = false;

    std::mutex decodeGate;
    MfaacDecoder aacDecoder;
    bool aacInitialized = false;

    std::atomic<float> volume{ 1.0f };
    std::atomic<bool> muted{ false };
    std::atomic<bool> stopping{ false };
    HANDLE stopEvent = nullptr;
    HANDLE audioEvent = nullptr;
    HANDLE wakeEvent = nullptr;

    ComPtr<IMMDeviceEnumerator> enumerator;
    ComPtr<IAudioClient> client;
    ComPtr<IAudioRenderClient> renderClient;
    ComPtr<ISimpleAudioVolume> sessionVolume;
    UINT32 bufferFrames = 0;
    bool eventDriven = false;
    std::thread worker;

    bool mfInit = false;

public:
    AudioEngine() : ring(kRingFrames * kChannels, 0) {
        stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        audioEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        wakeEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    }

    ~AudioEngine() { shutdown(); }

    HRESULT initialize(const wchar_t* deviceId, float initialVolume, bool initialMute) noexcept {
        volume.store(std::clamp(initialVolume, 0.0f, 1.0f));
        muted.store(initialMute);
        if (deviceId && *deviceId) {
            std::lock_guard<std::mutex> lock(deviceGate);
            targetDeviceId = deviceId;
        }
        mfInit = SUCCEEDED(MFStartup(MF_VERSION, MFSTARTUP_LITE));

        std::promise<HRESULT> readyPromise;
        auto readyFuture = readyPromise.get_future();
        worker = std::thread([this, p = std::move(readyPromise)]() mutable {
            renderLoop(std::move(p));
        });
        return readyFuture.get();
    }

    void shutdown() noexcept {
        stopping.store(true);
        if (stopEvent) SetEvent(stopEvent);
        if (wakeEvent) SetEvent(wakeEvent);
        if (worker.joinable()) worker.join();

        {
            ComScope com;
            std::lock_guard<std::mutex> lock(decodeGate);
            aacDecoder.reset();
        }
        if (mfInit) { MFShutdown(); mfInit = false; }

        if (wakeEvent) { CloseHandle(wakeEvent); wakeEvent = nullptr; }
        if (audioEvent) { CloseHandle(audioEvent); audioEvent = nullptr; }
        if (stopEvent) { CloseHandle(stopEvent); stopEvent = nullptr; }
    }

    void pushPcm(const void* data, uint32_t byteLength) noexcept {
        if (!data || byteLength < kBlockAlign || stopping.load()) return;
        const size_t frames = byteLength / kBlockAlign;
        const auto* samples = static_cast<const int16_t*>(data);

        std::lock_guard<std::mutex> lock(ringGate);
        if (frames >= kRingFrames) {
            const size_t skip = frames - kRingFrames;
            samples += skip * kChannels;
            head = tail = 0;
            count = kRingFrames;
            std::memcpy(ring.data(), samples, kRingFrames * kBlockAlign);
            prerolling = false;
            return;
        }

        // Trim oldest samples gently if a burst pushes buffering past the sync watermark.
        if (count + frames > kRingFrames) {
            size_t drop = (count + frames) - kRingFrames;
            head = (head + drop) % kRingFrames;
            count -= drop;
        } else if (count + frames > kHighWaterFrames) {
            size_t drop = (count + frames) - kTargetTrimFrames;
            if (drop < count) {
                head = (head + drop) % kRingFrames;
                count -= drop;
            }
        }

        const size_t firstPart = std::min(frames, kRingFrames - tail);
        std::memcpy(&ring[tail * kChannels], samples, firstPart * kBlockAlign);
        if (frames > firstPart) {
            std::memcpy(ring.data(), samples + firstPart * kChannels, (frames - firstPart) * kBlockAlign);
        }
        tail = (tail + frames) % kRingFrames;
        count += frames;
        if (prerolling && count >= prerollGoal)
            prerolling = false;
    }

    HRESULT pushAacLc(const uint8_t* bytes, uint32_t length) noexcept {
        if (!bytes || length == 0 || stopping.load()) return S_OK;
        ComScope com;
        std::lock_guard<std::mutex> lock(decodeGate);
        if (!aacInitialized) {
            if (!aacDecoder.initialize()) return E_FAIL;
            aacInitialized = true;
        }
        return aacDecoder.decode(bytes, length, [this](const BYTE* pcm, DWORD cb) {
            pushPcm(pcm, cb);
        });
    }

    void flush() noexcept {
        {
            std::lock_guard<std::mutex> lock(ringGate);
            head = tail = count = 0;
            prerolling = true;
            prerollGoal = kPrerollFrames;
            lastSampleL = lastSampleR = 0;
        }
        {
            ComScope com;
            std::lock_guard<std::mutex> lock(decodeGate);
            if (aacInitialized) aacDecoder.flush();
        }
    }

    void setVolume(float vol) noexcept {
        float clamped = std::clamp(vol, 0.0f, 1.0f);
        volume.store(clamped);
        std::lock_guard<std::mutex> lock(deviceGate);
        if (sessionVolume) {
            sessionVolume->SetMasterVolume(clamped, nullptr);
            sessionVolume->SetMute(muted.load() || clamped <= 0.0001f ? TRUE : FALSE, nullptr);
        }
    }

    void setMute(bool mute) noexcept {
        muted.store(mute);
        std::lock_guard<std::mutex> lock(deviceGate);
        if (sessionVolume) {
            float v = volume.load();
            sessionVolume->SetMute(mute || v <= 0.0001f ? TRUE : FALSE, nullptr);
        }
    }

    void setDevice(const wchar_t* deviceId) noexcept {
        {
            std::lock_guard<std::mutex> lock(deviceGate);
            targetDeviceId = (deviceId && *deviceId) ? deviceId : L"";
            rebindRequested = true;
        }
        if (wakeEvent) SetEvent(wakeEvent);
    }

private:
    void closeAudioClient() noexcept {
        std::lock_guard<std::mutex> lock(deviceGate);
        if (client) client->Stop();
        sessionVolume.Reset();
        renderClient.Reset();
        client.Reset();
        bufferFrames = 0;
        eventDriven = false;
    }

    HRESULT openAudioClient() noexcept {
        ComPtr<IMMDevice> dev;
        std::wstring wantedId;
        {
            std::lock_guard<std::mutex> lock(deviceGate);
            wantedId = targetDeviceId;
        }

        HRESULT hr = E_FAIL;
        if (!wantedId.empty())
            hr = enumerator->GetDevice(wantedId.c_str(), &dev);
        if (FAILED(hr) || !dev)
            hr = enumerator->GetDefaultAudioEndpoint(eRender, eMultimedia, &dev);
        if (FAILED(hr) || !dev) return hr;

        ComPtr<IAudioClient> newClient;
        hr = dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
            reinterpret_cast<void**>(newClient.GetAddressOf()));
        if (FAILED(hr)) return hr;

        WAVEFORMATEX wfx = {};
        wfx.wFormatTag = WAVE_FORMAT_PCM;
        wfx.nChannels = kChannels;
        wfx.nSamplesPerSec = kSampleRate;
        wfx.wBitsPerSample = 16;
        wfx.nBlockAlign = kBlockAlign;
        wfx.nAvgBytesPerSec = kSampleRate * kBlockAlign;

        DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                      AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        bool useEvents = true;
        hr = newClient->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 200000, 0, &wfx, nullptr);
        if (FAILED(hr)) {
            useEvents = false;
            flags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            hr = newClient->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 300000, 0, &wfx, nullptr);
            if (FAILED(hr)) return hr;
        }
        if (useEvents && audioEvent) {
            ResetEvent(audioEvent);
            if (FAILED(newClient->SetEventHandle(audioEvent))) useEvents = false;
        }

        UINT32 buf = 0;
        hr = newClient->GetBufferSize(&buf);
        if (FAILED(hr) || buf == 0) return FAILED(hr) ? hr : E_FAIL;

        ComPtr<IAudioRenderClient> rc;
        hr = newClient->GetService(IID_PPV_ARGS(&rc));
        if (FAILED(hr)) return hr;

        ComPtr<ISimpleAudioVolume> vol;
        newClient->GetService(IID_PPV_ARGS(&vol));
        if (vol) {
            float v = volume.load();
            vol->SetMasterVolume(v, nullptr);
            vol->SetMute(muted.load() || v <= 0.0001f ? TRUE : FALSE, nullptr);
        }

        ComPtr<IAudioSessionControl> ctrl;
        if (SUCCEEDED(newClient->GetService(IID_PPV_ARGS(&ctrl))) && ctrl)
            ctrl->SetDisplayName(L"NotAirplay Audio", nullptr);

        BYTE* initialBuf = nullptr;
        if (SUCCEEDED(rc->GetBuffer(buf, &initialBuf)))
            rc->ReleaseBuffer(buf, AUDCLNT_BUFFERFLAGS_SILENT);

        hr = newClient->Start();
        if (FAILED(hr)) return hr;

        {
            std::lock_guard<std::mutex> lock(deviceGate);
            client = std::move(newClient);
            renderClient = std::move(rc);
            sessionVolume = std::move(vol);
            bufferFrames = buf;
            eventDriven = useEvents;
            rebindRequested = false;
        }
        return S_OK;
    }

    HRESULT pumpOnce() noexcept {
        ComPtr<IAudioClient> localClient;
        ComPtr<IAudioRenderClient> localRender;
        UINT32 buf = 0;
        {
            std::lock_guard<std::mutex> lock(deviceGate);
            if (!client || !renderClient) return AUDCLNT_E_DEVICE_INVALIDATED;
            localClient = client;
            localRender = renderClient;
            buf = bufferFrames;
        }

        UINT32 padding = 0;
        HRESULT hr = localClient->GetCurrentPadding(&padding);
        if (FAILED(hr)) return hr;
        if (padding >= buf) return S_OK;

        const UINT32 available = buf - padding;
        BYTE* outPtr = nullptr;
        hr = localRender->GetBuffer(available, &outPtr);
        if (FAILED(hr)) return hr;

        bool silenceFlag = false;
        if (muted.load()) {
            std::lock_guard<std::mutex> lock(ringGate);
            const size_t consume = std::min<size_t>(available, count);
            head = (head + consume) % kRingFrames;
            count -= consume;
            lastSampleL = lastSampleR = 0;
            silenceFlag = true;
        } else {
            auto* dst = reinterpret_cast<int16_t*>(outPtr);
            std::lock_guard<std::mutex> lock(ringGate);
            if (prerolling || count == 0) {
                if (!prerolling && count == 0) {
                    prerolling = true;
                    prerollGoal = kRecoverPrerollFrames;
                }
                lastSampleL = lastSampleR = 0;
                silenceFlag = true;
            } else {
                const size_t take = std::min<size_t>(available, count);
                const size_t firstPart = std::min(take, kRingFrames - head);
                std::memcpy(dst, &ring[head * kChannels], firstPart * kBlockAlign);
                if (take > firstPart) {
                    std::memcpy(dst + firstPart * kChannels, ring.data(), (take - firstPart) * kBlockAlign);
                }
                head = (head + take) % kRingFrames;
                count -= take;
                lastSampleL = dst[(take - 1) * kChannels];
                lastSampleR = dst[(take - 1) * kChannels + 1];

                if (take < available) {
                    // Graceful underrun tail fade-out prevents clicks when Wi-Fi stalls.
                    const size_t rem = available - take;
                    const size_t fadeLen = std::min(rem, kUnderrunFadeSamples);
                    auto* tailPtr = dst + take * kChannels;
                    for (size_t i = 0; i < fadeLen; ++i) {
                        const float gain = static_cast<float>(fadeLen - 1 - i) / static_cast<float>(fadeLen);
                        tailPtr[i * 2] = static_cast<int16_t>(lastSampleL * gain);
                        tailPtr[i * 2 + 1] = static_cast<int16_t>(lastSampleR * gain);
                    }
                    if (rem > fadeLen) {
                        std::memset(tailPtr + fadeLen * kChannels, 0, (rem - fadeLen) * kBlockAlign);
                    }
                    lastSampleL = lastSampleR = 0;
                    prerolling = true;
                    prerollGoal = kRecoverPrerollFrames;
                }
            }
        }

        return localRender->ReleaseBuffer(available, silenceFlag ? AUDCLNT_BUFFERFLAGS_SILENT : 0);
    }

    void renderLoop(std::promise<HRESULT> readyPromise) noexcept {
        ComScope com;
        HRESULT hrInit = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator));
        if (SUCCEEDED(hrInit))
            hrInit = openAudioClient();
        readyPromise.set_value(hrInit);
        if (FAILED(hrInit)) {
            closeAudioClient();
            enumerator.Reset();
            return;
        }

        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
        HMODULE avrt = LoadLibraryW(L"avrt.dll");
        HANDLE mmcss = nullptr;
        if (avrt) {
            using AvSetFn = HANDLE(WINAPI*)(LPCWSTR, LPDWORD);
            auto setFn = reinterpret_cast<AvSetFn>(reinterpret_cast<void*>(GetProcAddress(avrt, "AvSetMmThreadCharacteristicsW")));
            DWORD idx = 0;
            if (setFn) mmcss = setFn(L"Pro Audio", &idx);
        }

        while (!stopping.load()) {
            bool needRebind = false;
            bool useEvents = false;
            {
                std::lock_guard<std::mutex> lock(deviceGate);
                needRebind = rebindRequested || !client;
                useEvents = eventDriven;
            }
            if (needRebind) {
                closeAudioClient();
                if (FAILED(openAudioClient())) {
                    if (WaitForSingleObject(stopEvent, 150) == WAIT_OBJECT_0) break;
                    continue;
                }
                std::lock_guard<std::mutex> lock(deviceGate);
                useEvents = eventDriven;
            }

            HRESULT hr = pumpOnce();
            if (hr == AUDCLNT_E_DEVICE_INVALIDATED) {
                closeAudioClient();
                if (WaitForSingleObject(stopEvent, 80) == WAIT_OBJECT_0) break;
                continue;
            }

            if (useEvents && audioEvent) {
                HANDLE handles[3] = { stopEvent, wakeEvent, audioEvent };
                DWORD wait = WaitForMultipleObjects(3, handles, FALSE, 25);
                if (wait == WAIT_OBJECT_0) break;
            } else {
                HANDLE handles[2] = { stopEvent, wakeEvent };
                DWORD wait = WaitForMultipleObjects(2, handles, FALSE, 10);
                if (wait == WAIT_OBJECT_0) break;
            }
        }

        if (avrt) {
            if (mmcss) {
                using AvRevertFn = BOOL(WINAPI*)(HANDLE);
                auto revertFn = reinterpret_cast<AvRevertFn>(reinterpret_cast<void*>(GetProcAddress(avrt, "AvRevertMmThreadCharacteristics")));
                if (revertFn) revertFn(mmcss);
            }
            FreeLibrary(avrt);
        }
        closeAudioClient();
        enumerator.Reset();
    }
};

} // namespace

#define API extern "C" __declspec(dllexport)

API HRESULT na_audio_create(const wchar_t* deviceId, float initialVolume, int initialMute, void** result) noexcept {
    if (!result) return E_INVALIDARG;
    *result = nullptr;
    auto engine = new (std::nothrow) AudioEngine();
    if (!engine) return E_OUTOFMEMORY;
    HRESULT hr = engine->initialize(deviceId, initialVolume, initialMute != 0);
    if (FAILED(hr)) {
        delete engine;
        return hr;
    }
    *result = engine;
    return S_OK;
}

API HRESULT na_audio_push_pcm(void* handle, const void* pcmBytes, DWORD byteLength) noexcept {
    if (!handle) return E_INVALIDARG;
    static_cast<AudioEngine*>(handle)->pushPcm(pcmBytes, byteLength);
    return S_OK;
}

API HRESULT na_audio_push_aac_lc(void* handle, const uint8_t* frameBytes, DWORD frameLength) noexcept {
    if (!handle) return E_INVALIDARG;
    return static_cast<AudioEngine*>(handle)->pushAacLc(frameBytes, frameLength);
}

API void na_audio_flush(void* handle) noexcept {
    if (handle) static_cast<AudioEngine*>(handle)->flush();
}

API void na_audio_set_volume(void* handle, float volume) noexcept {
    if (handle) static_cast<AudioEngine*>(handle)->setVolume(volume);
}

API void na_audio_set_mute(void* handle, int mute) noexcept {
    if (handle) static_cast<AudioEngine*>(handle)->setMute(mute != 0);
}

API void na_audio_set_device(void* handle, const wchar_t* deviceId) noexcept {
    if (handle) static_cast<AudioEngine*>(handle)->setDevice(deviceId);
}

API void na_audio_destroy(void* handle) noexcept {
    delete static_cast<AudioEngine*>(handle);
}

API int na_audio_supports_aac_lc() noexcept {
    MfApartment apt;
    MfaacDecoder probe;
    bool ok = probe.initialize();
    probe.reset();
    return ok ? 1 : 0;
}

API int na_audio_enum_devices(char* utf8Out, int maxBytes) noexcept {
    if (!utf8Out || maxBytes <= 1) return 0;
    utf8Out[0] = '\0';
    MfApartment apt;
    std::string result;
    {
        ComPtr<IMMDeviceEnumerator> enumerator;
        if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator))))
            return 0;
        ComPtr<IMMDeviceCollection> collection;
        if (FAILED(enumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &collection)) || !collection)
            return 0;
        UINT count = 0;
        if (FAILED(collection->GetCount(&count))) return 0;
        for (UINT i = 0; i < count; ++i) {
            ComPtr<IMMDevice> device;
            if (FAILED(collection->Item(i, &device)) || !device) continue;
            LPWSTR id = nullptr;
            if (FAILED(device->GetId(&id)) || !id) continue;
            ComPtr<IPropertyStore> props;
            std::wstring friendly = id;
            if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &props)) && props) {
                PROPVARIANT pv;
                PropVariantInit(&pv);
                if (SUCCEEDED(props->GetValue(PKEY_Device_FriendlyName, &pv)) && pv.vt == VT_LPWSTR && pv.pwszVal)
                    friendly = pv.pwszVal;
                PropVariantClear(&pv);
            }
            auto toUtf8 = [](const wchar_t* s) -> std::string {
                int len = WideCharToMultiByte(CP_UTF8, 0, s, -1, nullptr, 0, nullptr, nullptr);
                if (len <= 1) return {};
                std::string out(static_cast<size_t>(len - 1), '\0');
                WideCharToMultiByte(CP_UTF8, 0, s, -1, out.data(), len - 1, nullptr, nullptr);
                for (char& ch : out) if (ch == '\t' || ch == '\n' || ch == '\r') ch = ' ';
                return out;
            };
            std::string uId = toUtf8(id);
            std::string uName = toUtf8(friendly.c_str());
            CoTaskMemFree(id);
            if (!uId.empty() && !uName.empty()) {
                result.append(uId);
                result.push_back('\t');
                result.append(uName);
                result.push_back('\n');
            }
        }
    }
    int copyLen = std::min<int>(static_cast<int>(result.size()), maxBytes - 1);
    if (copyLen > 0) std::memcpy(utf8Out, result.data(), static_cast<size_t>(copyLen));
    utf8Out[copyLen] = '\0';
    return copyLen;
}
