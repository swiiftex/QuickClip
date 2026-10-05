// C API used by the QuickClip app (see QuickClip/Recording/CaptureEngine.cs).
#include "Engine.h"

#include <cstdarg>
#include <cstdio>
#include <shared_mutex>

namespace
{
    typedef void(__stdcall* LogCallback)(const char* message);
    LogCallback g_log = nullptr;

    std::shared_mutex g_engineLock;   // Save holds it shared; start/stop exclusively
    std::unique_ptr<qc::Engine> g_engine;

    void CopyError(const std::string& message, wchar_t* error, int length)
    {
        if (!error || length <= 0) return;
        wcsncpy_s(error, static_cast<size_t>(length), qc::Widen(message).c_str(), _TRUNCATE);
    }

    void FfmpegLog(void* avcl, int level, const char* format, va_list args)
    {
        if (level > AV_LOG_WARNING || !g_log) return;
        char line[1024];
        int prefix = 1;
        av_log_format_line(avcl, level, format, args, line, sizeof line, &prefix);
        size_t n = strlen(line);
        while (n > 0 && (line[n - 1] == '\n' || line[n - 1] == '\r')) line[--n] = 0;
        if (n > 0) qc::Log("ffmpeg: %s", line);
    }
}

namespace qc
{
    void Log(const char* format, ...)
    {
        if (!g_log) return;
        char buffer[2048];
        va_list args;
        va_start(args, format);
        vsnprintf(buffer, sizeof buffer, format, args);
        va_end(args);
        g_log(buffer);
    }

    std::string AvError(int err)
    {
        char buffer[AV_ERROR_MAX_STRING_SIZE] = {};
        av_strerror(err, buffer, sizeof buffer);
        return buffer;
    }

    std::string Narrow(const std::wstring& s)
    {
        if (s.empty()) return {};
        int n = WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0, nullptr, nullptr);
        std::string out(static_cast<size_t>(n), '\0');
        WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), out.data(), n, nullptr, nullptr);
        return out;
    }

    std::wstring Widen(const std::string& s)
    {
        if (s.empty()) return {};
        int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
        std::wstring out(static_cast<size_t>(n), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), out.data(), n);
        return out;
    }
}

extern "C"
{
    __declspec(dllexport) void qc_set_log(LogCallback callback)
    {
        g_log = callback;
        av_log_set_callback(callback ? FfmpegLog : av_log_default_callback);
    }

    __declspec(dllexport) int qc_enum_outputs(QcOutput* outputs, int max)
    {
        return qc::EnumOutputs(outputs, max);
    }

    __declspec(dllexport) int qc_enum_microphones(QcAudioDevice* devices, int max)
    {
        return qc::EnumMicrophones(devices, max);
    }

    __declspec(dllexport) int qc_start(const QcConfig* config, wchar_t* error, int errorLength)
    {
        std::unique_lock guard(g_engineLock);
        g_engine.reset();
        auto engine = std::make_unique<qc::Engine>(*config);
        std::string message;
        if (!engine->Start(message))
        {
            CopyError(message, error, errorLength);
            return 0;
        }
        g_engine = std::move(engine);
        return 1;
    }

    __declspec(dllexport) void qc_stop()
    {
        std::unique_lock guard(g_engineLock);
        g_engine.reset();
    }

    __declspec(dllexport) void qc_set_chat_processes(const uint32_t* pids, int count)
    {
        std::shared_lock guard(g_engineLock);
        if (!g_engine) return;
        std::vector<DWORD> list(pids, pids + (count > 0 ? count : 0));
        g_engine->SetChatProcesses(list);
    }

    __declspec(dllexport) int qc_save(const wchar_t* path, int seconds, const wchar_t* title, wchar_t* error, int errorLength)
    {
        std::shared_lock guard(g_engineLock);
        if (!g_engine)
        {
            CopyError("Recording is off.", error, errorLength);
            return 0;
        }
        std::string message;
        if (!g_engine->Save(path, seconds, title ? title : L"", message))
        {
            CopyError(message, error, errorLength);
            return 0;
        }
        return 1;
    }

    __declspec(dllexport) int qc_get_stats(QcStats* stats)
    {
        std::shared_lock guard(g_engineLock);
        *stats = {};
        if (!g_engine) return 0;
        g_engine->GetStats(*stats);
        return 1;
    }
}
