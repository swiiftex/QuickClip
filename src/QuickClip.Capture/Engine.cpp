#include "Engine.h"

#include <dxgi1_2.h>
#include <timeapi.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <set>

namespace qc
{
    // ---------------------------------------------------------------------------------------------
    // PacketRing
    // ---------------------------------------------------------------------------------------------

    void PacketRing::Push(AVPacket* packet, int stream, int64_t timeUs, bool key)
    {
        std::lock_guard guard(lock_);
        entries_.push_back({ packet, stream, timeUs, key });
        bytes_ += packet->size;
        if (stream == 0) newestVideoUs_ = std::max(newestVideoUs_, timeUs);
        // Keep a little more than the window so a keyframe at or before its start is always there.
        int64_t cutoff = newestVideoUs_ - windowUs_ - 2000000;
        while (!entries_.empty() && entries_.front().timeUs < cutoff)
        {
            bytes_ -= entries_.front().packet->size;
            av_packet_free(&entries_.front().packet);
            entries_.pop_front();
        }
    }

    std::vector<PacketRing::Entry> PacketRing::Snapshot(int64_t fromUs, int64_t toUs, int64_t& startUs)
    {
        std::lock_guard guard(lock_);
        int64_t firstKey = -1, start = -1;
        for (const auto& e : entries_)
        {
            if (e.stream != 0 || !e.key) continue;
            if (firstKey < 0) firstKey = e.timeUs;
            if (e.timeUs <= fromUs) start = e.timeUs;
            else break;
        }
        if (start < 0) start = firstKey;
        std::vector<Entry> result;
        startUs = start;
        if (start < 0) return result;
        for (const auto& e : entries_)
            if (e.timeUs >= start && e.timeUs <= toUs)
                result.push_back({ av_packet_clone(e.packet), e.stream, e.timeUs, e.key });
        return result;
    }

    int64_t PacketRing::NewestVideoUs()
    {
        std::lock_guard guard(lock_);
        return newestVideoUs_;
    }

    void PacketRing::Stats(int64_t& bytes, double& seconds)
    {
        std::lock_guard guard(lock_);
        bytes = bytes_;
        int64_t oldest = -1;
        for (const auto& e : entries_)
            if (e.stream == 0) { oldest = e.timeUs; break; }
        seconds = oldest < 0 ? 0 : std::min<double>((newestVideoUs_ - oldest) / 1e6, windowUs_ / 1e6);
    }

    void PacketRing::Clear()
    {
        std::lock_guard guard(lock_);
        for (auto& e : entries_) av_packet_free(&e.packet);
        entries_.clear();
        bytes_ = 0;
        newestVideoUs_ = -1;
    }

    // ---------------------------------------------------------------------------------------------
    // Monitors
    // ---------------------------------------------------------------------------------------------

    int EnumOutputs(QcOutput* outputs, int max)
    {
        IDXGIFactory1* factory = nullptr;
        if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(&factory)))) return 0;
        int count = 0;
        IDXGIAdapter1* adapter = nullptr;
        for (UINT a = 0; factory->EnumAdapters1(a, &adapter) != DXGI_ERROR_NOT_FOUND; a++)
        {
            DXGI_ADAPTER_DESC1 ad{};
            adapter->GetDesc1(&ad);
            IDXGIOutput* output = nullptr;
            for (UINT o = 0; adapter->EnumOutputs(o, &output) != DXGI_ERROR_NOT_FOUND; o++)
            {
                DXGI_OUTPUT_DESC od{};
                output->GetDesc(&od);
                output->Release();
                if (!od.AttachedToDesktop) continue;
                if (count < max && outputs)
                {
                    QcOutput& q = outputs[count];
                    q.adapter = static_cast<int>(a);
                    q.output = static_cast<int>(o);
                    q.left = od.DesktopCoordinates.left;
                    q.top = od.DesktopCoordinates.top;
                    q.right = od.DesktopCoordinates.right;
                    q.bottom = od.DesktopCoordinates.bottom;
                    wcsncpy_s(q.deviceName, od.DeviceName, _TRUNCATE);
                    wcsncpy_s(q.adapterName, ad.Description, _TRUNCATE);
                }
                count++;
            }
            adapter->Release();
        }
        factory->Release();
        return count;
    }

    namespace
    {
        struct MonitorTarget
        {
            int adapter = -1, output = -1;
            uint64_t hmonitor = 0;
            int width = 0, height = 0;
        };

        bool FindMonitor(const std::wstring& device, MonitorTarget& target)
        {
            IDXGIFactory1* factory = nullptr;
            if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(&factory)))) return false;
            bool found = false;
            IDXGIAdapter1* adapter = nullptr;
            for (UINT a = 0; !found && factory->EnumAdapters1(a, &adapter) != DXGI_ERROR_NOT_FOUND; a++)
            {
                IDXGIOutput* output = nullptr;
                for (UINT o = 0; !found && adapter->EnumOutputs(o, &output) != DXGI_ERROR_NOT_FOUND; o++)
                {
                    DXGI_OUTPUT_DESC od{};
                    output->GetDesc(&od);
                    output->Release();
                    bool primary = od.DesktopCoordinates.left == 0 && od.DesktopCoordinates.top == 0;
                    bool match = device.empty() ? primary : _wcsicmp(device.c_str(), od.DeviceName) == 0;
                    if (od.AttachedToDesktop && match)
                    {
                        target.adapter = static_cast<int>(a);
                        target.output = static_cast<int>(o);
                        target.hmonitor = reinterpret_cast<uint64_t>(od.Monitor);
                        target.width = od.DesktopCoordinates.right - od.DesktopCoordinates.left;
                        target.height = od.DesktopCoordinates.bottom - od.DesktopCoordinates.top;
                        found = true;
                    }
                }
                adapter->Release();
            }
            factory->Release();
            return found;
        }

        struct Area
        {
            int x = 0, y = 0, width = 0, height = 0;
        };

        /// The centered part of a monitor with the shape aw:ah (the whole monitor for 0 or when it already has that
        /// shape), a multiple of 8 wide (AMD's AV1 encoder pads other widths) and even in height. Mirrors
        /// RecordingPresets.Area in the app.
        Area CenterArea(int mw, int mh, int aw, int ah)
        {
            int w = mw, h = mh;
            if (aw > 0 && ah > 0 && mw > 0 && mh > 0)
            {
                double ratio = static_cast<double>(aw) / ah, monitorRatio = static_cast<double>(mw) / mh;
                if (std::abs(monitorRatio / ratio - 1) >= 0.01)
                {
                    if (monitorRatio > ratio) w = static_cast<int>(mh * ratio);
                    else h = static_cast<int>(mw / ratio);
                }
            }
            w &= ~7;
            h &= ~1;
            return { ((mw - w) / 2) & ~1, ((mh - h) / 2) & ~1, w, h };
        }

        /// Notices when the picture has gone flat (a solid image encodes to keyframes of a few hundred bytes, real
        /// screen content to tens or hundreds of KB). A capture that has gone stale after a display change, a sleep
        /// or a game switching modes can keep sending black frames; starting it again brings the picture back.
        class FlatWatch
        {
        public:
            /// Call for each keyframe; true when the capture should be started again.
            bool Keyframe(int bytes, int flatBelow, int64_t nowUs)
            {
                if (bytes >= flatBelow)
                {
                    flatSinceUs_ = -1;
                    retryUs_ = FirstRetryUs; // real picture: a later flat spell gets the quick check again
                    return false;
                }
                if (flatSinceUs_ < 0) flatSinceUs_ = nowUs;
                return nowUs - flatSinceUs_ >= retryUs_;
            }

            /// After a restart for flatness: if the picture is still flat (a genuinely black screen), wait longer.
            void Restarted()
            {
                flatSinceUs_ = -1;
                retryUs_ = std::min<int64_t>(retryUs_ * 4, 300'000'000);
            }

            int64_t RetryUs() const { return retryUs_; }

        private:
            static constexpr int64_t FirstRetryUs = 5'000'000;
            int64_t flatSinceUs_ = -1;
            int64_t retryUs_ = FirstRetryUs;
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Video
    // ---------------------------------------------------------------------------------------------

    /// State shared with the video thread. Held by shared_ptr so a capture call that never returns
    /// (e.g. a static screen with window capture) can't touch freed memory after Stop gives up on it.
    struct Engine::Shared
    {
        explicit Shared(int64_t windowUs) : ring(windowUs) {}

        std::wstring monitorDevice;
        int aspectWidth = 0, aspectHeight = 0;     // record the centered part with this shape; 0 = the whole monitor
        int fps = 60, outWidth = 0, outHeight = 0, videoKbps = 0;
        bool cursor = true;
        std::string encoder;
        int64_t t0 = 0;

        std::atomic<bool> stop{ false };
        HANDLE videoStarted = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        std::string startError;

        // Capture threads. A restart starts a new one with the next generation; an older one stops as soon as it
        // notices, and never adds to the ring again (it may be stuck in a call that doesn't return for a while).
        std::atomic<int> generation{ 0 };
        std::mutex videoLock;                      // held while a capture thread checks its generation and pushes
        std::atomic<int> runningVideoThreads{ 0 };
        std::atomic<int64_t> lastFrameUs{ 0 };     // engine clock; 0 until the first frame
        std::atomic<bool> usingDuplication{ true };
        std::atomic<bool> usingFallback{ false };  // Graphics Capture only because Desktop Duplication wasn't available
        std::atomic<bool> rebuildRequested{ false };
        std::atomic<int> testFreezeMs{ 0 };        // tests: make the capture thread hang this long
        std::atomic<int> testFlatBelow{ 0 };       // tests: treat keyframes smaller than this as flat

        PacketRing ring;
        std::mutex infoLock;
        std::shared_ptr<StreamInfo> video;

        std::atomic<int> width{ 0 }, height{ 0 };
        std::atomic<int64_t> dropped{ 0 };
        std::mutex statsLock;
        std::deque<int64_t> frameTimes;

        ~Shared() { CloseHandle(videoStarted); }
    };

    namespace
    {
        struct VideoPipe
        {
            AVFilterGraph* graph = nullptr;
            AVFilterContext* sink = nullptr;
            AVCodecContext* encoder = nullptr;
            bool duplication = false;
            bool fallback = false; // Graphics Capture only because Desktop Duplication wasn't available

            ~VideoPipe() { Close(); }

            void Close()
            {
                avcodec_free_context(&encoder);
                avfilter_graph_free(&graph);
                sink = nullptr;
            }

            bool TryBuild(const char* source, const std::string& args, AVBufferRef* device, std::string& error)
            {
                avfilter_graph_free(&graph);
                graph = avfilter_graph_alloc();
                AVFilterContext* src = avfilter_graph_alloc_filter(graph, avfilter_get_by_name(source), "src");
                if (!src) { error = std::string(source) + " is not available in this FFmpeg build"; return false; }
                src->hw_device_ctx = av_buffer_ref(device);
                int ret = avfilter_init_str(src, args.c_str());
                if (ret >= 0)
                {
                    sink = avfilter_graph_alloc_filter(graph, avfilter_get_by_name("buffersink"), "sink");
                    ret = avfilter_init_str(sink, nullptr);
                }
                if (ret >= 0) ret = avfilter_link(src, 0, sink, 0);
                if (ret >= 0) ret = avfilter_graph_config(graph, nullptr);
                if (ret < 0)
                {
                    error = std::string("Starting screen capture (") + source + ") failed: " + AvError(ret);
                    avfilter_graph_free(&graph);
                    sink = nullptr;
                    return false;
                }
                return true;
            }

            bool Build(const Engine::Shared& s, const MonitorTarget& m, const Area& a, AVBufferRef* device, std::string& error)
            {
                bool scaled = s.outWidth > 0 && (s.outWidth != a.width || s.outHeight != a.height);
                // Both sources crop on the GPU: Desktop Duplication copies just that rectangle, Graphics Capture
                // samples (and scales) only the area inside its crop margins.
                char dda[256], wgc[384];
                snprintf(dda, sizeof dda, "output_idx=%d:framerate=%d:draw_mouse=%d:video_size=%dx%d:offset_x=%d:offset_y=%d",
                    m.output, s.fps, s.cursor ? 1 : 0, a.width, a.height, a.x, a.y);
                snprintf(wgc, sizeof wgc, "hmonitor=%llu:max_framerate=%d:capture_cursor=%d:width=%d:height=%d:resize_mode=scale"
                    ":crop_left=%d:crop_top=%d:crop_right=%d:crop_bottom=%d",
                    static_cast<unsigned long long>(m.hmonitor), s.fps, s.cursor ? 1 : 0, scaled ? s.outWidth : 0, scaled ? s.outHeight : 0,
                    a.x, a.y, m.width - a.x - a.width, m.height - a.y - a.height);

                // Desktop Duplication is cheapest and keeps a constant frame rate, but can't scale;
                // Windows Graphics Capture scales on the GPU. Each is the other's fallback.
                std::string first, second;
                fallback = false;
                if (!scaled && TryBuild("ddagrab", dda, device, first)) { duplication = true; return true; }
                if (TryBuild("gfxcapture", wgc, device, second))
                {
                    duplication = false;
                    fallback = !scaled;
                    if (fallback) Log("desktop duplication unavailable (%s); using graphics capture for now", first.c_str());
                    return true;
                }
                if (scaled && TryBuild("ddagrab", dda, device, first))
                {
                    Log("scaling unavailable (%s); recording at the area's native size", second.c_str());
                    duplication = true;
                    return true;
                }
                error = first.empty() ? second : first;
                return false;
            }

            bool OpenEncoder(const Engine::Shared& s, std::string& error)
            {
                const AVCodec* codec = avcodec_find_encoder_by_name(s.encoder.c_str());
                if (!codec) { error = "Encoder " + s.encoder + " is not available"; return false; }
                encoder = avcodec_alloc_context3(codec);
                encoder->width = av_buffersink_get_w(sink);
                encoder->height = av_buffersink_get_h(sink);
                encoder->pix_fmt = static_cast<AVPixelFormat>(av_buffersink_get_format(sink));
                encoder->hw_frames_ctx = av_buffer_ref(av_buffersink_get_hw_frames_ctx(sink));
                encoder->time_base = AVRational{ 1, s.fps };
                encoder->framerate = AVRational{ s.fps, 1 };
                encoder->gop_size = s.fps; // a keyframe every second: clips start close to the requested length
                encoder->max_b_frames = 0;
                encoder->bit_rate = static_cast<int64_t>(s.videoKbps) * 1000;
                encoder->rc_max_rate = static_cast<int64_t>(s.videoKbps) * 1500;
                encoder->rc_buffer_size = s.videoKbps * 2000;
                encoder->color_range = AVCOL_RANGE_MPEG;
                encoder->colorspace = AVCOL_SPC_BT709;
                encoder->color_primaries = AVCOL_PRI_BT709;
                encoder->color_trc = AVCOL_TRC_BT709;
                encoder->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;

                AVDictionary* options = nullptr;
                const std::string& name = s.encoder;
                // Keyframes forced by the clock (see VideoLoop) must be real ones that a clip can start on.
                av_dict_set(&options, name.find("_nvenc") != std::string::npos || name == "libx264" ? "forced-idr" : "forced_idr", "1", 0);
                if (name.find("_amf") != std::string::npos)
                {
                    av_dict_set(&options, "usage", "transcoding", 0);
                    av_dict_set(&options, "quality", "balanced", 0);
                    av_dict_set(&options, "rc", "vbr_peak", 0);
                }
                else if (name.find("_nvenc") != std::string::npos)
                {
                    av_dict_set(&options, "preset", "p4", 0);
                    av_dict_set(&options, "tune", "hq", 0);
                    av_dict_set(&options, "rc", "vbr", 0);
                }
                else if (name.find("_qsv") != std::string::npos)
                {
                    av_dict_set(&options, "preset", "medium", 0);
                }
                int ret = avcodec_open2(encoder, codec, &options);
                av_dict_free(&options);
                if (ret < 0)
                {
                    error = "Opening encoder " + name + " failed: " + AvError(ret);
                    avcodec_free_context(&encoder);
                    return false;
                }
                return true;
            }
        };

        /// One capture thread: captures, encodes and fills the ring until stopped or replaced by a newer generation.
        /// Every (re)start looks the monitor up again and opens a new graphics device, since a display change can
        /// move, resize or invalidate the old ones.
        void VideoLoop(std::shared_ptr<Engine::Shared> s, int generation)
        {
            s->runningVideoThreads++;
            CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            // The capture source paces frames with short sleeps; at the default 15.6 ms timer granularity
            // a 60 fps frame interval often turns into two.
            timeBeginPeriod(1);
            AVBufferRef* device = nullptr;
            AVFrame* frame = av_frame_alloc();
            AVPacket* packet = av_packet_alloc();
            VideoPipe pipe;
            FlatWatch flat;
            bool reportStart = generation == 0; // the first thread tells Start how it went
            std::string lastError;
            int64_t lastIndex = -1, lastKeyIndex = -1;
            int64_t retryDuplicationUs = 0, retryDuplicationWait = 5'000'000;
            int duplicationBusy = 0;
            AVRational sourceBase{ 1, 1000000 };
            int64_t sourceOffsetUs = INT64_MIN; // maps the capture source's timestamps onto the engine clock
            auto current = [&] { return !s->stop && s->generation == generation; };
            auto closeCapture = [&] {
                pipe.Close();
                av_buffer_unref(&device);
            };

            while (current())
            {
                if (!pipe.graph)
                {
                    std::string error;
                    MonitorTarget monitor;
                    Area area;
                    bool ok = FindMonitor(s->monitorDevice, monitor);
                    if (!ok) error = "The selected monitor wasn't found.";
                    if (ok)
                    {
                        area = CenterArea(monitor.width, monitor.height, s->aspectWidth, s->aspectHeight);
                        int ret = av_hwdevice_ctx_create(&device, AV_HWDEVICE_TYPE_D3D11VA, std::to_string(monitor.adapter).c_str(), nullptr, 0);
                        if (ret < 0)
                        {
                            ok = false;
                            error = "Couldn't open the graphics card: " + AvError(ret);
                        }
                    }
                    ok = ok && pipe.Build(*s, monitor, area, device, error);
                    // Desktop Duplication is refused while an older capture thread still holds it; that one usually
                    // lets go within moments, so try again briefly before settling for Graphics Capture.
                    if (ok && pipe.fallback && duplicationBusy < 3 && current())
                    {
                        duplicationBusy++;
                        closeCapture();
                        Sleep(500);
                        continue;
                    }
                    duplicationBusy = 0;
                    ok = ok && pipe.OpenEncoder(*s, error);
                    if (!ok)
                    {
                        closeCapture();
                        if (reportStart)
                        {
                            s->startError = error;
                            SetEvent(s->videoStarted);
                            break;
                        }
                        if (error != lastError) Log("starting capture again failed: %s", error.c_str());
                        lastError = error;
                        Sleep(1000);
                        continue;
                    }
                    lastError.clear();

                    auto info = std::make_shared<StreamInfo>();
                    info->params = avcodec_parameters_alloc();
                    avcodec_parameters_from_context(info->params, pipe.encoder);
                    info->timeBase = pipe.encoder->time_base;
                    {
                        std::scoped_lock guard(s->videoLock, s->infoLock);
                        if (!current()) break;
                        // Packets from a differently sized encoder can't share a file with the new ones.
                        if (s->video && (s->video->params->width != info->params->width || s->video->params->height != info->params->height))
                            s->ring.Clear();
                        s->video = info;
                    }
                    s->width = pipe.encoder->width;
                    s->height = pipe.encoder->height;
                    s->usingDuplication = pipe.duplication;
                    s->usingFallback = pipe.fallback;
                    lastKeyIndex = -1;
                    if (pipe.fallback) retryDuplicationUs = NowUs() + retryDuplicationWait;
                    else retryDuplicationWait = 5'000'000;
                    s->lastFrameUs = NowUs(); // the watchdog counts from here until the first frame
                    sourceBase = av_buffersink_get_time_base(pipe.sink);
                    sourceOffsetUs = INT64_MIN;
                    Log("capturing %dx%d at %d,%d of a %dx%d monitor with %s via %s", pipe.encoder->width, pipe.encoder->height,
                        area.x, area.y, monitor.width, monitor.height, s->encoder.c_str(), pipe.duplication ? "desktop duplication" : "graphics capture");
                    if (reportStart)
                    {
                        reportStart = false;
                        SetEvent(s->videoStarted);
                    }
                }

                // Tests: hang the way a capture call that doesn't return would.
                if (int freeze = s->testFreezeMs.exchange(0); freeze > 0) Sleep(freeze);

                int ret = av_buffersink_get_frame(pipe.sink, frame);
                if (!current())
                {
                    av_frame_unref(frame);
                    break;
                }
                if (ret < 0)
                {
                    Log("capture interrupted (%s); starting it again", AvError(ret).c_str());
                    closeCapture();
                    Sleep(500);
                    continue;
                }
                if (s->rebuildRequested.exchange(false))
                {
                    av_frame_unref(frame);
                    Log("the display changed; starting capture again");
                    closeCapture();
                    continue;
                }

                // Place the frame on the fixed frame-rate grid by when it was captured. The source's own
                // timestamps are exact; anchor them to the engine clock once (again if they ever drift apart).
                int64_t now = NowUs();
                int64_t captureUs = now - s->t0;
                if (frame->pts != AV_NOPTS_VALUE)
                {
                    int64_t sourceUs = av_rescale_q(frame->pts, sourceBase, AVRational{ 1, 1000000 });
                    if (sourceOffsetUs == INT64_MIN || std::llabs(sourceUs + sourceOffsetUs - captureUs) > 200000)
                        sourceOffsetUs = captureUs - sourceUs;
                    captureUs = sourceUs + sourceOffsetUs;
                }
                // A frame that's a little late still takes the next slot; only a real gap skips slots.
                double slot = static_cast<double>(captureUs) * s->fps / 1e6;
                int64_t index;
                if (lastIndex < 0) index = std::llround(slot);
                else if (slot < lastIndex + 0.5) index = -1;                // same slot as the previous frame
                else if (slot < lastIndex + 2.0) index = lastIndex + 1;
                else index = std::llround(slot);
                if (index < 0)
                {
                    av_frame_unref(frame);
                    continue;
                }
                if (lastIndex >= 0 && index > lastIndex + 1 && pipe.duplication) s->dropped += index - lastIndex - 1;
                lastIndex = index;
                frame->pts = index;
                // A keyframe at least once a second by the clock, not only every `fps` frames: when the picture barely
                // changes Graphics Capture sends few frames, and a clip can only start on a keyframe.
                if (lastKeyIndex < 0 || index - lastKeyIndex >= s->fps)
                {
                    frame->pict_type = AV_PICTURE_TYPE_I;
                    lastKeyIndex = index;
                }
                else frame->pict_type = AV_PICTURE_TYPE_NONE;
                ret = avcodec_send_frame(pipe.encoder, frame);
                av_frame_unref(frame);
                if (ret < 0)
                {
                    Log("encoding failed (%s); starting capture again", AvError(ret).c_str());
                    closeCapture();
                    continue;
                }
                bool flatPicture = false;
                while (avcodec_receive_packet(pipe.encoder, packet) == 0)
                {
                    bool key = (packet->flags & AV_PKT_FLAG_KEY) != 0;
                    int flatBelow = s->testFlatBelow > 0 ? s->testFlatBelow.load() : std::max(4096, pipe.encoder->width * pipe.encoder->height / 1000);
                    if (key && flat.Keyframe(packet->size, flatBelow, now)) flatPicture = true;
                    int64_t timeUs = packet->pts * 1000000 / s->fps;
                    {
                        std::lock_guard guard(s->videoLock);
                        if (s->generation == generation) s->ring.Push(av_packet_clone(packet), 0, timeUs, key);
                    }
                    av_packet_unref(packet);
                }
                s->lastFrameUs = now;
                {
                    std::lock_guard guard(s->statsLock);
                    s->frameTimes.push_back(now);
                    while (!s->frameTimes.empty() && s->frameTimes.front() < now - 1000000) s->frameTimes.pop_front();
                }
                if (flatPicture)
                {
                    Log("the picture has been a flat color for %.0f s; starting capture again", flat.RetryUs() / 1e6);
                    flat.Restarted();
                    closeCapture();
                }
                else if (pipe.fallback && now >= retryDuplicationUs)
                {
                    // Desktop Duplication may be free again (an old capture that held it has finished).
                    Log("trying desktop duplication again");
                    retryDuplicationWait = std::min<int64_t>(retryDuplicationWait * 2, 60'000'000);
                    closeCapture();
                }
            }

            closeCapture();
            av_packet_free(&packet);
            av_frame_free(&frame);
            timeEndPeriod(1);
            CoUninitialize();
            s->runningVideoThreads--;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Engine
    // ---------------------------------------------------------------------------------------------

    Engine::Engine(const QcConfig& c)
        : shared_(std::make_shared<Shared>(static_cast<int64_t>(c.bufferSeconds) * 1000000)),
          monitor_(c.monitorDevice ? c.monitorDevice : L""),
          micDevice_(c.micDeviceId ? c.micDeviceId : L""),
          splitChat_(c.splitChat != 0), splitMusic_(c.splitMusic != 0), micEnabled_(c.micEnabled != 0), audioKbps_(c.audioKbps)
    {
        shared_->fps = std::clamp(c.fps, 10, 240);
        shared_->outWidth = c.outWidth & ~7;
        shared_->outHeight = c.outHeight & ~1;
        shared_->aspectWidth = std::max(0, c.aspectWidth);
        shared_->aspectHeight = std::max(0, c.aspectHeight);
        shared_->videoKbps = c.videoKbps;
        shared_->cursor = c.captureCursor != 0;
        shared_->encoder = c.encoder ? c.encoder : "libx264";
    }

    bool Engine::Start(std::string& error)
    {
        MonitorTarget monitor;
        if (!FindMonitor(monitor_, monitor))
        {
            error = "The selected monitor wasn't found.";
            return false;
        }
        shared_->monitorDevice = monitor_;
        shared_->t0 = NowUs();

        auto addTrack = [&](const char* title) {
            auto track = std::make_unique<Track>();
            track->title = title;
            track->mixer = std::make_unique<TrackMixer>(shared_->t0);
            if (!track->encoder.Open(audioKbps_, error)) return -1;
            tracks_.push_back(std::move(track));
            return static_cast<int>(tracks_.size()) - 1;
        };
        if (addTrack("Desktop") < 0) return false;
        if (splitChat_ && (chatTrack_ = addTrack("Chat")) < 0) return false;
        if (splitMusic_ && (musicTrack_ = addTrack("Music")) < 0) return false;
        if (micEnabled_ && (micTrack_ = addTrack("Mic")) < 0) return false;

        videoThread_ = std::thread(VideoLoop, shared_, 0);
        WaitForSingleObject(shared_->videoStarted, 15000);
        if (!shared_->startError.empty() || !shared_->video)
        {
            error = shared_->startError.empty() ? "Screen capture didn't start." : shared_->startError;
            return false;
        }

        // Until the app reports its chat and music apps: everything except QuickClip's own previews.
        SetAudioProcesses(GetCurrentProcessId(), {}, {}, {});
        if (micEnabled_)
            micSource_ = std::make_unique<AudioSource>(SourceKind::Microphone, 0, micDevice_, *tracks_[micTrack_]->mixer);
        audioThread_ = std::thread([this] { AudioLoop(); });
        watchThread_ = std::thread([this] { WatchLoop(); });
        return true;
    }

    void Engine::RequestRebuild()
    {
        shared_->rebuildRequested = true;
    }

    void Engine::TestFreezeVideo(int ms)
    {
        shared_->testFreezeMs = ms;
    }

    void Engine::TestFlatBelow(int bytes)
    {
        shared_->testFlatBelow = bytes;
    }

    /// Starts a new capture thread; the old one may be stuck in a call that doesn't return, and ends by itself
    /// (without touching the ring again) once it does.
    void Engine::RestartVideo()
    {
        int generation;
        {
            std::lock_guard guard(shared_->videoLock);
            generation = ++shared_->generation;
        }
        shared_->lastFrameUs = NowUs();
        if (videoThread_.joinable()) videoThread_.detach();
        videoThread_ = std::thread(VideoLoop, shared_, generation);
    }

    /// Watches for the picture stopping: a capture call stuck after a display change or a game switching modes
    /// would otherwise leave clips with sound but no video.
    void Engine::WatchLoop()
    {
        int64_t lastRestartUs = 0, retryUs = 3'000'000;
        while (!shared_->stop)
        {
            Sleep(250);
            int64_t now = NowUs(), quiet = now - shared_->lastFrameUs;
            if (quiet < 1'000'000) retryUs = 3'000'000;
            // Desktop Duplication repeats the last frame while nothing changes, so it never goes quiet; Graphics
            // Capture only sends a frame when something does. As a stand-in for Desktop Duplication, though, it
            // shouldn't last: starting again moves back to Desktop Duplication once that's free.
            int64_t limit = shared_->usingDuplication ? retryUs
                : std::max<int64_t>(retryUs, shared_->usingFallback ? 5'000'000 : 15'000'000);
            if (shared_->lastFrameUs == 0 || quiet < limit || now - lastRestartUs < limit) continue;
            if (shared_->runningVideoThreads >= 4) continue; // several are stuck already; wait for one to come back
            Log("no picture for %.1f s; starting capture again", quiet / 1e6);
            RestartVideo();
            lastRestartUs = now;
            retryUs = std::min<int64_t>(retryUs * 2, 60'000'000);
        }
    }

    Engine::~Engine()
    {
        shared_->stop = true;
        if (watchThread_.joinable()) watchThread_.join();
        if (audioThread_.joinable()) audioThread_.join();
        {
            std::lock_guard guard(sourcesLock_);
            sources_.clear();
        }
        micSource_.reset();
        // A capture call returns with the next frame; with a frozen screen that may take a while.
        for (int i = 0; i < 150 && shared_->runningVideoThreads > 0; i++) Sleep(20);
        if (videoThread_.joinable())
        {
            if (shared_->runningVideoThreads == 0) videoThread_.join();
            else videoThread_.detach();
        }
    }

    void Engine::SetAudioProcesses(DWORD desktopExclude, const std::vector<DWORD>& desktopInclude,
        const std::vector<DWORD>& chat, const std::vector<DWORD>& music)
    {
        std::set<SourceKey> wanted;
        if (PerAppCaptureAvailable())
        {
            if (desktopExclude != 0) wanted.insert({ SourceKind::ProcessExclude, desktopExclude, 0 });
            else for (DWORD pid : desktopInclude) wanted.insert({ SourceKind::ProcessInclude, pid, 0 });
            if (chatTrack_ >= 0) for (DWORD pid : chat) wanted.insert({ SourceKind::ProcessInclude, pid, chatTrack_ });
            if (musicTrack_ >= 0) for (DWORD pid : music) wanted.insert({ SourceKind::ProcessInclude, pid, musicTrack_ });
        }
        else
            wanted.insert({ SourceKind::ProcessExclude, GetCurrentProcessId(), 0 }); // falls back to all system audio

        std::lock_guard guard(sourcesLock_);
        size_t stopped = std::erase_if(sources_, [&](const auto& source) { return !wanted.contains(source.first); });
        // Old streams stop before new ones start: a moment of silence rather than the same sound twice.
        size_t started = 0;
        for (const auto& key : wanted)
            if (!sources_.contains(key))
            {
                sources_.emplace(key, std::make_unique<AudioSource>(key.kind, key.pid, L"", *tracks_[static_cast<size_t>(key.track)]->mixer));
                started++;
            }
        if (stopped > 0 || started > 0)
            Log("app audio changed: desktop %s, %zu chat and %zu music apps (%zu streams stopped, %zu started)",
                desktopExclude != 0 ? "is all but one app" : "is recorded app by app", chat.size(), music.size(), stopped, started);
    }

    void Engine::AudioLoop()
    {
        std::vector<float> samples;
        const int64_t latency = SampleRate / 5; // wait 200 ms for late packets before encoding a block
        while (!shared_->stop)
        {
            Sleep(20);
            int64_t upTo = (NowUs() - shared_->t0) * SampleRate / 1000000 - latency;
            for (size_t i = 0; i < tracks_.size(); i++)
            {
                tracks_[i]->mixer->Take(upTo, samples);
                int stream = static_cast<int>(i) + 1;
                tracks_[i]->encoder.Encode(samples, [&](AVPacket* p) {
                    shared_->ring.Push(p, stream, p->pts * 1000000 / SampleRate, true);
                });
            }
        }
    }

    bool Engine::Save(const std::wstring& path, int seconds, const std::wstring& title, std::string& error)
    {
        // The clip ends now, when it was asked for. Audio is encoded ~200 ms behind, so wait for every
        // track to get past this moment (they always do unless something is badly stuck).
        int64_t endUs = NowUs() - shared_->t0;
        int64_t endSample = endUs * SampleRate / 1000000;
        for (int i = 0; i < 50; i++)
        {
            bool ready = std::all_of(tracks_.begin(), tracks_.end(), [&](const auto& t) { return t->mixer->Position() >= endSample + 2048; });
            if (ready) break;
            Sleep(20);
        }
        if (shared_->ring.NewestVideoUs() < 0) { error = "Nothing has been recorded yet."; return false; }
        int64_t startUs = 0;
        auto entries = shared_->ring.Snapshot(endUs - static_cast<int64_t>(seconds) * 1000000, endUs, startUs);
        if (entries.empty()) { error = "Nothing has been recorded yet."; return false; }
        std::stable_sort(entries.begin(), entries.end(), [](const auto& a, const auto& b) { return a.timeUs < b.timeUs; });

        std::shared_ptr<StreamInfo> video;
        {
            std::lock_guard guard(shared_->infoLock);
            video = shared_->video;
        }

        auto freeEntries = [&] { for (auto& e : entries) av_packet_free(&e.packet); };
        std::string utf8 = Narrow(path);
        AVFormatContext* out = nullptr;
        int ret = avformat_alloc_output_context2(&out, nullptr, "mp4", utf8.c_str());
        if (ret < 0) { error = "Couldn't create the clip: " + AvError(ret); freeEntries(); return false; }

        AVStream* vs = avformat_new_stream(out, nullptr);
        avcodec_parameters_copy(vs->codecpar, video->params);
        vs->codecpar->codec_tag = 0;
        vs->time_base = video->timeBase;
        vs->avg_frame_rate = AVRational{ shared_->fps, 1 };
        std::vector<AVRational> sourceBase{ video->timeBase };
        for (const auto& track : tracks_)
        {
            AVStream* as = avformat_new_stream(out, nullptr);
            avcodec_parameters_from_context(as->codecpar, track->encoder.Context());
            as->codecpar->codec_tag = 0;
            as->time_base = AVRational{ 1, SampleRate };
            av_dict_set(&as->metadata, "title", track->title.c_str(), 0);
            av_dict_set(&as->metadata, "handler_name", track->title.c_str(), 0);
            sourceBase.push_back(AVRational{ 1, SampleRate });
        }
        if (!title.empty()) av_dict_set(&out->metadata, "title", Narrow(title).c_str(), 0);
        av_dict_set(&out->metadata, "comment", "Recorded with QuickClip", 0);

        ret = avio_open(&out->pb, utf8.c_str(), AVIO_FLAG_WRITE);
        AVDictionary* options = nullptr;
        av_dict_set(&options, "movflags", "+faststart", 0);
        if (ret >= 0) ret = avformat_write_header(out, &options);
        av_dict_free(&options);

        for (auto& e : entries)
        {
            AVPacket* p = e.packet;
            if (ret >= 0)
            {
                AVRational src = sourceBase[e.stream];
                int64_t offset = av_rescale_q(startUs, AVRational{ 1, 1000000 }, src);
                p->stream_index = e.stream;
                if (p->pts != AV_NOPTS_VALUE) p->pts -= offset;
                if (p->dts != AV_NOPTS_VALUE) p->dts -= offset;
                av_packet_rescale_ts(p, src, out->streams[e.stream]->time_base);
                ret = av_interleaved_write_frame(out, p);
            }
            av_packet_free(&e.packet);
        }
        if (ret >= 0) ret = av_write_trailer(out);
        avio_closep(&out->pb);
        avformat_free_context(out);
        if (ret < 0)
        {
            error = "Writing the clip failed: " + AvError(ret);
            DeleteFileW(path.c_str());
            return false;
        }
        return true;
    }

    void Engine::GetStats(QcStats& stats)
    {
        stats.running = 1;
        stats.width = shared_->width;
        stats.height = shared_->height;
        {
            std::lock_guard guard(shared_->statsLock);
            stats.fps = static_cast<double>(shared_->frameTimes.size());
        }
        shared_->ring.Stats(stats.bufferBytes, stats.bufferSeconds);
        stats.droppedFrames = shared_->dropped;
    }
}
