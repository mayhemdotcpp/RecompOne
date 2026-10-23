using System.Diagnostics;
using Silk.NET.OpenAL;
using ALDevice = Silk.NET.OpenAL.Device;
using ALCtx = Silk.NET.OpenAL.Context;

namespace RecompOne.Runtime.Host;

internal static unsafe class Audio
{
    private static ALContext? _alc;
    private static AL? _al;
    private static ALDevice* _device;
    private static ALCtx* _context;

    private const int FramesPerBuffer = 256;
    private const int NumBuffers = 32;
    private const double TargetLatency = 0.05;
    private const double MaxLatency = 0.15;
    private const double LevelSmoothing = 1.0;
    private const double PitchPerSecond = 0.1;
    private const double MaxPitchSkew = 0.005;
    private const int FeedIntervalMs = 2;

    private static uint _source;
    private static readonly uint[] _buffers = new uint[NumBuffers];
    private static readonly Stack<uint> _free = new(NumBuffers);
    private static readonly short[] _chunk = new short[FramesPerBuffer * 2];

    private static Thread? _mixerThread;
    private static volatile Spu? _spu;
    private static volatile bool _running;
    private static float _masterVolume = 1.0f;

    private static double _level;
    private static float _pitch = 1f;

    public static void Initialize()
    {
        try
        {
            _alc = ALContext.GetApi(true);
            _al = AL.GetApi(true);
            _device = _alc.OpenDevice("");
            if (_device == null)
            {
                Console.Error.WriteLine("[Host] no audio device, audio disabled");
                return;
            }

            _context = _alc.CreateContext(_device, null);
            _alc.MakeContextCurrent(_context);

            _source = _al.GenSource();
            _al.SetSourceProperty(_source, SourceFloat.Gain, _masterVolume);
            fixed (uint* ptr = _buffers)
            {
                _al.GenBuffers(NumBuffers, ptr);
            }

            foreach (var b in _buffers) _free.Push(b);

            _running = true;
            _mixerThread = new Thread(MixerLoop) { IsBackground = true, Name = "spu-mixer" };
            _mixerThread.Start();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] audio init failed: {e.Message}");
        }
    }

    public static void Attach(Spu? spu)
    {
        if (spu == null) return;
        _spu = spu;
        spu.VoiceGain = Config.ConfigManager.Game.SpuVolume;
        spu.XaGain = Config.ConfigManager.Game.XaVolume;
    }

    public static void Detach()
    {
        _spu = null;
    }

    public static void SetMasterVolume(float volume)
    {
        _masterVolume = Math.Clamp(volume, 0f, 1f);
        if (_al != null && _source != 0)
            _al.SetSourceProperty(_source, SourceFloat.Gain, _masterVolume);
    }

    private static void MixerLoop()
    {
        var clock = Stopwatch.StartNew();
        var last = 0.0;
        while (_running)
        {
            var now = clock.Elapsed.TotalSeconds;
            Feed(_spu, now - last);
            last = now;
            Thread.Sleep(FeedIntervalMs);
        }
    }

    private static void Feed(Spu? spu, double dt)
    {
        Reclaim(out var playing);
        var queued = QueuedFrames();

        while (spu != null && spu.TakeOutput(_chunk))
        {
            if (_free.Count == 0 || queued + FramesPerBuffer > MaxLatency * Spu.SampleRate) continue;

            var buf = _free.Pop();
            _al!.BufferData(buf, BufferFormat.Stereo16, _chunk, Spu.SampleRate);
            _al.SourceQueueBuffers(_source, 1, &buf);
            queued += FramesPerBuffer;
        }

        var level = (queued + (spu?.OutputBacklog ?? 0)) / (double)Spu.SampleRate;
        if (!playing)
        {
            if (level < TargetLatency) return;
            _level = level;
            SetPitch(1f);
            _al!.SourcePlay(_source);
            return;
        }

        _level += (level - _level) * Math.Min(1.0, dt / LevelSmoothing);
        SetPitch((float)(1.0 + Math.Clamp((_level - TargetLatency) * PitchPerSecond, -MaxPitchSkew, MaxPitchSkew)));
    }

    private static void Reclaim(out bool playing)
    {
        _al!.GetSourceProperty(_source, GetSourceInteger.SourceState, out var state);
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out var processed);
        for (; processed > 0; processed--)
        {
            uint buf = 0;
            _al.SourceUnqueueBuffers(_source, 1, &buf);
            _free.Push(buf);
        }

        if (state == (int)SourceState.Stopped) _al.SourceRewind(_source);
        playing = state == (int)SourceState.Playing;
    }

    private static int QueuedFrames()
    {
        _al!.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out var buffers);
        _al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out var offset);
        return buffers * FramesPerBuffer - offset;
    }

    private static void SetPitch(float pitch)
    {
        if (Math.Abs(pitch - _pitch) < 1e-5f) return;
        _pitch = pitch;
        _al!.SetSourceProperty(_source, SourceFloat.Pitch, pitch);
    }

    public static void Shutdown()
    {
        if (_alc == null) return;
        _running = false;
        _mixerThread?.Join();
        if (_al != null)
        {
            _al.SourceStop(_source);
            _al.DeleteSource(_source);
            _al.DeleteBuffers(_buffers);
        }

        if (_context != null) _alc.DestroyContext(_context);
        if (_device != null) _alc.CloseDevice(_device);
    }
}