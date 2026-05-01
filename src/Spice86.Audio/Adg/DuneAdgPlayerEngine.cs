namespace Spice86.Audio.Adg;

using System;

/// <summary>
/// DUNE ADG (AdLib Gold / OPL3) music player engine.
/// Faithfully ports the <c>AdgDriverCode.cs</c> driver logic for standalone use.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>DuneAdgPlayerEngine</c> in OpenRakis/Cryogenic (branch
/// <c>reverse/adlib_gold_driver</c>). All 18-channel ADG / OPL3 Gold routing,
/// timing, envelope, pitch-bend, and volume-modulation logic is preserved exactly.
/// </para>
/// <para>
/// OPL3 register writes are dispatched via the <see cref="RegisterWriteRequested"/>
/// event; the caller is responsible for connecting a chip emulator (e.g. NukedOPL3Sharp)
/// and an audio output backend.
/// </para>
/// <para>
/// Typical usage:
/// <code>
/// var engine = new DuneAdgPlayerEngine();
/// engine.RegisterWriteRequested += (reg, val) =&gt; opl.Write(reg, val);
/// engine.LoadSong(File.ReadAllBytes("ARRAKIS.ADG"));
/// engine.Play();
///
/// // Inside your audio render callback (called from a separate thread):
/// engine.AdvanceSamples(frameCount, sampleRate);
/// </code>
/// </para>
/// </remarks>
public sealed partial class DuneAdgPlayerEngine : IDisposable {
    // ── Audio pipeline ──────────────────────────────────────────────────────
    private volatile bool _disposed;
    private volatile bool _playing;
    private volatile bool _paused;
    private readonly object _lock = new();
    private long _totalTickCount;

    // ── PIT timing ──────────────────────────────────────────────────────────
    private const int PitInputClock = 1_193_182;
    private int _pitReloadValue = 0x1745;
    private long _sampleAccumulator;

    // ── Song data ───────────────────────────────────────────────────────────
    private byte[] _songData = Array.Empty<byte>();
    private int _dataBase;
    private ushort _eventBase;

    // ── Global driver state ─────────────────────────────────────────────────
    private byte _statusFlags;
    private ushort _measure;
    private byte _subdivision;
    private byte _currentVolume = 0xFF;
    private byte _targetVolume  = 0xFF;
    private byte _masterVolume  = 0xFF;
    private ushort _fadeBitPattern;

    // ── Per-channel state (18 OPL3 Gold channels) ───────────────────────────
    /// <summary>Number of OPL3 Gold channels managed by the driver.</summary>
    public const int ChannelCount = 18;

    private readonly ushort[] _channelWait                   = new ushort[ChannelCount];
    private readonly ushort[] _channelEventPointer           = new ushort[ChannelCount];
    private readonly ushort[] _channelStartOffset            = new ushort[ChannelCount];
    private readonly byte[]   _channelInstrument             = new byte[ChannelCount];
    private readonly byte[]   _channelNote                   = new byte[ChannelCount];
    private readonly ushort[] _channelPitchMode              = new ushort[ChannelCount];
    private readonly byte[]   _channelPitchTranspose         = new byte[ChannelCount];
    private readonly byte[]   _channelPitchBendCounter       = new byte[ChannelCount];
    private readonly byte[]   _channelPitchBendCounterInit   = new byte[ChannelCount];
    private readonly byte[]   _channelPitchAccumulator       = new byte[ChannelCount];
    private readonly ushort[] _channelTlShaping              = new ushort[ChannelCount];
    private readonly ushort[] _channelEnvShaping             = new ushort[ChannelCount];
    private readonly ushort[] _channelCurrentOperatorLevel   = new ushort[ChannelCount];
    private readonly ushort[] _channelConnShaping            = new ushort[ChannelCount];
    private readonly ushort[] _channelVolModShaping          = new ushort[ChannelCount];
    private readonly ushort[] _channelConnModulation         = new ushort[ChannelCount];
    private readonly byte[]   _channelConnectionCurrent      = new byte[ChannelCount];
    private readonly byte[]   _channelPatchType              = new byte[ChannelCount];
    private readonly ushort[] _channelStateScratch           = new ushort[ChannelCount];
    private readonly ushort[] _channelFrequencyWord          = new ushort[ChannelCount];

    // ── Channel routing (18 logical → OPL3 Gold physical) ──────────────────
    private readonly byte[] _channelRoutingTable  = new byte[ChannelCount];
    private readonly byte[] _channelRouteShadow   = new byte[ChannelCount];
    private readonly byte[] _channelPrimaryRoute  = new byte[ChannelCount];
    private readonly byte[] _channelSecondaryRoute = new byte[ChannelCount];

    // ── Loop snapshot ───────────────────────────────────────────────────────
    private readonly ushort[] _snapshotWait    = new ushort[ChannelCount];
    private readonly ushort[] _snapshotPointer = new ushort[ChannelCount];

    // ── Global ADG state ────────────────────────────────────────────────────
    private ushort _fadeScratch;
    private ushort _fadeScratch2;
    private byte   _surroundMask = 0xFF;
    private byte   _tickEnabled;
    private byte   _loopCounter;

    /// <summary>
    /// 16-bit tempo accumulator. Hi8 is decremented every PIT tick; <see cref="ProcessTick"/>
    /// fires when Hi8 reaches 0, then reloads Hi8 by adding tempoWord.
    /// Mirrors <c>AdgTempoAccumulatorOffset</c> (0x0126).
    /// </summary>
    private ushort _tempoAccumulator;

    // ── Static lookup tables ─────────────────────────────────────────────────

    /// <summary>
    /// Runtime channel-route seed table from DNADG memory image at 564B:017F.
    /// Captured from dump memory bytes at linear 0x5662F.
    /// </summary>
    private static readonly byte[] InitialChannelRoutes = {
        0x08, 0x07, 0x07, 0x06, 0x88, 0x88, 0x87, 0x86, 0x82,
        0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88
    };

    /// <summary>
    /// Runtime route-shadow seed table from DNADG memory image at 564B:0191.
    /// Captured from dump memory bytes at linear 0x56641.
    /// </summary>
    private static readonly byte[] InitialRouteShadows = {
        0x0B, 0x0A, 0x0A, 0x09, 0x8B, 0x8B, 0x8A, 0x89, 0x85,
        0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88
    };

    /// <summary>
    /// Runtime primary-operator route seed table from DNADG 564B:01A3.
    /// Captured from dump memory bytes at linear 0x56653.
    /// </summary>
    private static readonly byte[] InitialPrimaryRoutes = {
        0x12, 0x11, 0x11, 0x10, 0x92, 0x92, 0x91, 0x90, 0x82,
        0x80, 0x81, 0x82, 0x88, 0x89, 0x8A, 0x90, 0x91, 0x92
    };

    /// <summary>
    /// Runtime secondary-operator route seed table from DNADG 564B:01B5.
    /// Captured from dump memory bytes at linear 0x56665.
    /// </summary>
    private static readonly byte[] InitialSecondaryRoutes = {
        0x15, 0x14, 0x14, 0x13, 0x95, 0x95, 0x94, 0x93, 0x85,
        0x93, 0x84, 0x85, 0x8B, 0x8C, 0x8D, 0x93, 0x94, 0x95
    };

    /// <summary>
    /// OPL3 F-number lookup table for one octave (C through B), 12 entries.
    /// Matches values in DNADG driver at <c>AdgFrequencyLookupTableOffset</c> = 0x0142.
    /// </summary>
    private static readonly ushort[] FrequencyLookupTable = {
        0x0157, 0x016C, 0x0181, 0x0198, 0x01B1, 0x01CB,
        0x01E6, 0x0203, 0x0222, 0x0243, 0x0266, 0x028A
    };

    /// <summary>
    /// Non-portamento pitch bend fractions (13 entries, indexed 0–12).
    /// From DNADG driver at <c>AdgPitchBendFractionsTableOffset</c> = 0x01C7.
    /// </summary>
    private static readonly byte[] PitchBendFractions = {
        0x13, 0x15, 0x15, 0x17, 0x19, 0x1A,
        0x1B, 0x1D, 0x1F, 0x21, 0x23, 0x24, 0x25
    };

    /// <summary>
    /// Portamento pitch bend fractions (two groups of 5: semitone 0–5 and 6–11).
    /// From DNADG driver at <c>AdgPortamentoFractionsTableOffset</c> = 0x01D4.
    /// </summary>
    private static readonly byte[] PortamentoFractions = {
        0x00, 0x05, 0x0A, 0x0F, 0x14,
        0x00, 0x06, 0x0C, 0x12, 0x18
    };

    // ── Public events ────────────────────────────────────────────────────────

    /// <summary>
    /// Fired whenever an OPL3 register must be written.
    /// First argument is the register address (0x000–0x1FF), second is the value.
    /// </summary>
    public event Action<ushort, byte>? RegisterWriteRequested;

    /// <summary>Fired when the song finishes playing.</summary>
    public event Action? SongFinished;

    /// <summary>
    /// Fired when a channel event is dispatched.
    /// Arguments: channel index, event name, detail string, total tick count.
    /// </summary>
    public event Action<int, string, string, long>? ChannelEventDispatched;

    /// <summary>
    /// Fired when the ADG driver requests a packed Gold volume change.
    /// The byte is a packed nibble volume value as used by the AdLib Gold hardware.
    /// </summary>
    public event Action<byte>? GoldVolumeChanged;

    // ── Public properties ────────────────────────────────────────────────────

    /// <summary>Current playback measure (1-based). Updated each tick.</summary>
    public int CurrentMeasure => _measure;

    /// <summary>Total ticks elapsed since playback started.</summary>
    public long TotalTickCount => _totalTickCount;

    /// <summary><see langword="true"/> when the engine is actively generating audio.</summary>
    public bool IsPlaying => _playing && !_paused;

    /// <summary><see langword="true"/> when playback state is preserved but audio is paused.</summary>
    public bool IsPaused => _playing && _paused;

    /// <summary>Current driver volume (packed nibble byte).</summary>
    public byte CurrentDriverVolume => _currentVolume;

    /// <summary>Target driver volume (packed nibble byte).</summary>
    public byte TargetDriverVolume => _targetVolume;

    /// <summary>Master driver volume (packed nibble byte).</summary>
    public byte MasterDriverVolume => _masterVolume;

    /// <summary>PIT timer reload value used for tick-rate calculation.</summary>
    public int PitReloadValue => _pitReloadValue;

    /// <summary>Tick rate in Hz derived from the PIT clock and current reload value.</summary>
    public double TickRateHz => (double)PitInputClock / _pitReloadValue;

    /// <summary>Gets parsed song header info. Only valid after <see cref="LoadSong"/>.</summary>
    public SongHeaderInfo? HeaderInfo { get; private set; }

    // ── Constructor ──────────────────────────────────────────────────────────

    /// <summary>Initialises the engine. Connect <see cref="RegisterWriteRequested"/> before calling <see cref="Play"/>.</summary>
    public DuneAdgPlayerEngine() { }

    // ── Channel snapshot ─────────────────────────────────────────────────────

    /// <summary>Gets a snapshot of per-channel state for UI display.</summary>
    public ChannelSnapshot[] GetChannelSnapshots() {
        ChannelSnapshot[] snapshots = new ChannelSnapshot[ChannelCount];
        lock (_lock) {
            for (int i = 0; i < ChannelCount; i++) {
                snapshots[i] = new ChannelSnapshot {
                    Channel    = i,
                    Wait       = _channelWait[i],
                    Instrument = _channelInstrument[i],
                    Note       = _channelNote[i],
                    Transpose  = _channelPitchTranspose[i],
                    Frequency  = _channelFrequencyWord[i],
                    PitchBendFlag = _channelPitchMode[i],
                    IsActive   = _channelEventPointer[i] != 0 && _channelWait[i] != 0xFFFF
                };
            }
        }
        return snapshots;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Loads a song from raw file bytes, automatically detecting and applying HSQ decompression.
    /// </summary>
    /// <param name="fileData">Raw ADG or HSQ-compressed ADG file bytes.</param>
    public void LoadSong(byte[] fileData) {
        byte[]? decompressed = TryDecompressHsq(fileData);
        byte[] data = decompressed ?? fileData;
        bool wasCompressed = decompressed != null;

        lock (_lock) {
            if (_playing) {
                StopInternal();
            }

            _songData   = data;
            _dataBase   = 2;
            _eventBase  = SongWord(0);

            HeaderInfo  = ParseSongHeader(data, _dataBase, _eventBase, wasCompressed);
        }
    }

    /// <summary>
    /// Sets the master volume from a two-component raw value.
    /// Mirrors <c>AdgSetVolume</c>.
    /// </summary>
    /// <param name="low">Low component (AL).</param>
    /// <param name="high">High component (AH).</param>
    public void SetVolume(byte low, byte high) {
        lock (_lock) {
            byte packed = ComputeAdgVolume(low, high);
            _masterVolume  = packed;
            _targetVolume  = packed;
            _currentVolume = packed;
            _fadeBitPattern = 0xFFFF;
            ApplyVolume(packed);
        }
    }

    /// <summary>
    /// Sets the fade dynamics. Mirrors <c>AdgSetDynamicsCurve</c>.
    /// </summary>
    /// <param name="fadeSpeed">Fade speed selector.</param>
    /// <param name="volumeLow">Low component of the target volume.</param>
    /// <param name="volumeHigh">High component of the target volume.</param>
    public void SetDynamics(ushort fadeSpeed, byte volumeLow, byte volumeHigh) {
        lock (_lock) {
            byte packed = ComputeAdgVolume(volumeLow, volumeHigh);
            _targetVolume = packed;

            ushort fade;
            if      (fadeSpeed < 0x0060) { fade = 0xFFFF; }
            else if (fadeSpeed < 0x00C0) { fade = 0xAAAA; }
            else if (fadeSpeed < 0x0180) { fade = 0x8888; }
            else if (fadeSpeed < 0x0300) { fade = 0x8080; }
            else                         { fade = 0x8000; }
            _fadeBitPattern = fade;

            if ((_statusFlags & 0x80) != 0) {
                _statusFlags = (byte)(_statusFlags | 0x40);
            }
        }
    }

    /// <summary>
    /// Starts or resumes playback of the loaded song.
    /// </summary>
    public void Play() {
        lock (_lock) {
            if (_songData.Length == 0) {
                return;
            }

            if (_playing && _paused) {
                _paused = false;
                return;
            }

            if (_playing) {
                StopInternal();
            }

            InitOplChip();
            InitializeRoutingTables();
            UpdateGoldSurround();
            BuildChannelTable();

            _tempoAccumulator = 0;
            _currentVolume    = _masterVolume;
            _targetVolume     = _masterVolume;
            ApplyVolume(_currentVolume);
            _loopCounter   = 0;
            _tickEnabled   = 1;
            _fadeScratch   = 0;
            _fadeScratch2  = 0;
            _surroundMask  = 0xFF;
            _totalTickCount = 0;

            ProcessTick();
            _statusFlags = 0x80;

            _playing = true;
            _paused  = false;
        }
    }

    /// <summary>Pauses playback while preserving the current driver state.</summary>
    public void Pause() {
        lock (_lock) {
            if (!_playing || _paused) { return; }
            _paused = true;
        }
    }

    /// <summary>Resumes playback after a <see cref="Pause"/>.</summary>
    public void Resume() {
        lock (_lock) {
            if (!_playing || !_paused) { return; }
            _paused = false;
        }
    }

    /// <summary>Stops playback and silences all channels.</summary>
    public void Stop() {
        lock (_lock) {
            StopInternal();
        }
    }

    /// <summary>
    /// Advances the PIT timing accumulator by <paramref name="frameCount"/> audio frames
    /// at the given <paramref name="sampleRate"/>. Call this from your audio render callback.
    /// </summary>
    /// <param name="frameCount">Number of audio frames to advance.</param>
    /// <param name="sampleRate">Audio sample rate in Hz.</param>
    public void AdvanceSamples(int frameCount, int sampleRate) {
        if (!_playing || _paused) { return; }
        lock (_lock) {
            if (!_playing || _paused) { return; }
            _sampleAccumulator += (long)frameCount * PitInputClock;
            long threshold = (long)sampleRate * _pitReloadValue;
            while (_sampleAccumulator >= threshold) {
                _sampleAccumulator -= threshold;
                TickInternal();
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        _playing  = false;
        _paused   = false;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static byte   Lo8(ushort value)  => (byte)(value & 0xFF);
    private static byte   Hi8(ushort value)  => (byte)(value >> 8);
    private static ushort Make16(byte lo, byte hi) => (ushort)(lo | (hi << 8));

    private static ushort RotateRight16(ushort value, int count) {
        int n = count & 0x0F;
        if (n == 0) { return value; }
        return (ushort)((value >> n) | (value << (16 - n)));
    }

    private byte SongByte(int offset)        => _songData[offset];
    private byte SongByte16(ushort offset)   => _songData[offset];

    private ushort SongWord(int offset) =>
        (ushort)(_songData[offset] | (_songData[offset + 1] << 8));

    private ushort SongWord16(ushort offset) =>
        Make16(SongByte16(offset), SongByte16((ushort)(offset + 1)));

    // ── Internal play/stop ───────────────────────────────────────────────────

    private void StopInternal() {
        _playing = false;
        _paused  = false;
        SilenceAllChannels();
        _statusFlags = 0;
    }

    private void ApplyVolume(byte packed) {
        GoldVolumeChanged?.Invoke(packed);
    }

    // ── PIT tick ─────────────────────────────────────────────────────────────

    private void TickInternal() {
        if ((_statusFlags & 0x80) == 0) { return; }

        byte tickDivider = Hi8(_tempoAccumulator);
        tickDivider--;
        _tempoAccumulator = Make16(Lo8(_tempoAccumulator), tickDivider);

        if (tickDivider == 0) {
            ProcessTick();
        }

        _totalTickCount++;

        if ((_statusFlags & 0x40) == 0) { return; }

        ushort fadePattern = _fadeBitPattern;
        bool carry = (fadePattern & 0x8000) != 0;
        fadePattern = (ushort)((fadePattern << 1) | (carry ? 1 : 0));
        _fadeBitPattern = fadePattern;

        if (carry) {
            FadeStep();
        }
    }

    // ── Volume ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes a packed ADG volume nibble byte from (al=low, ah=high) components.
    /// Faithfully ports <c>AdgComputeScaledVolumeFromAx</c> from 564B:056E.
    /// </summary>
    private static byte ComputeAdgVolume(byte al, byte ah) {
        al = (byte)(al >> 1);
        al = (byte)(al >> 1);
        al = (byte)(al >> 1);
        byte dl = al;
        byte dh = ah;
        const byte bl = 0x78;
        const byte bh = 0xF0;

        if (ah > bl) { ah = bl; }

        ushort axDiv = Make16(0, ah);
        al = (byte)(axDiv / bh);
        ah = (byte)(axDiv % bh);

        ushort mul1 = (ushort)(al * dl);
        al = Lo8(mul1);
        ah = Hi8(mul1);

        byte temp = ah;
        ah = dh;
        dh = temp;

        ah = (byte)(ah - bh);
        ah = (byte)(0 - ah);
        if (ah > bl) { ah = bl; }

        axDiv = Make16(0, ah);
        al = (byte)(axDiv / bh);
        ah = (byte)(axDiv % bh);

        ushort axOut = (ushort)(al * dl);
        axOut = (ushort)(axOut >> 1);
        axOut = (ushort)(axOut >> 1);
        axOut = (ushort)(axOut >> 1);
        axOut = (ushort)(axOut >> 1);

        ah = dh;
        axOut = (ushort)(axOut & 0x0FF0);
        return Lo8((ushort)(axOut | ah));
    }

    // ── Header parsing ───────────────────────────────────────────────────────

    private SongHeaderInfo ParseSongHeader(byte[] data, int dataBase, int eventBase, bool wasCompressed) {
        int safeEventBase = (eventBase >= 0 && eventBase <= data.Length) ? eventBase : data.Length;

        SongHeaderInfo info = new SongHeaderInfo {
            RawFileSize       = data.Length,
            WasHsqCompressed  = wasCompressed,
            DataBase          = dataBase,
            EventBase         = eventBase,
            InstrumentCount   = safeEventBase > dataBase + 0x32
                                    ? (data.Length - safeEventBase) / 0x28
                                    : 0
        };

        if (data.Length >= dataBase + 0x32) {
            info.Tempo            = SongWord(dataBase + 0x30);
            info.LoopStartMeasure = SongWord(dataBase + 0x2A);
            info.LoopEndMeasure   = SongWord(dataBase + 0x2C);
            info.LoopCount        = SongWord(dataBase + 0x2E);

            int channelsToRead = Math.Min(ChannelCount, info.ChannelOffsets.Length);
            for (int i = 0; i < channelsToRead; i++) {
                int offset = dataBase + i * 2;
                if (offset + 1 >= data.Length) { break; }
                ushort relative = SongWord(offset);
                info.ChannelOffsets[i] = relative;
                info.ChannelActive[i]  = relative != 0;
            }
        }

        int active = 0;
        for (int i = 0; i < info.ChannelActive.Length; i++) {
            if (info.ChannelActive[i]) { active++; }
        }
        info.ActiveChannelCount = active;
        return info;
    }

    /// <summary>
    /// Extracts song header info from raw file data without loading the full song.
    /// Handles both HSQ-compressed and raw ADG data.
    /// </summary>
    /// <param name="fileData">Raw or HSQ-compressed ADG file bytes.</param>
    /// <param name="headerInfo">Parsed header on success; <see langword="null"/> otherwise.</param>
    /// <returns><see langword="true"/> if a header could be extracted.</returns>
    public static bool TryExtractHeaderInfo(byte[] fileData, out SongHeaderInfo? headerInfo) {
        headerInfo = null;
        if (fileData.Length < 2) { return false; }

        byte[]? decompressed = TryDecompressHsq(fileData);
        byte[] data = decompressed ?? fileData;
        bool wasCompressed = decompressed != null;

        if (data.Length < 2) { return false; }

        int dataBase  = 2;
        int eventBase = (ushort)(data[0] | (data[1] << 8));
        int safeEvent = (eventBase >= 0 && eventBase <= data.Length) ? eventBase : data.Length;

        SongHeaderInfo info = new SongHeaderInfo {
            RawFileSize      = data.Length,
            WasHsqCompressed = wasCompressed,
            DataBase         = dataBase,
            EventBase        = eventBase,
            InstrumentCount  = safeEvent > dataBase + 0x32
                                   ? (data.Length - safeEvent) / 0x28
                                   : 0
        };

        if (data.Length >= dataBase + 0x32) {
            info.Tempo            = (ushort)(data[dataBase + 0x30] | (data[dataBase + 0x31] << 8));
            info.LoopStartMeasure = (ushort)(data[dataBase + 0x2A] | (data[dataBase + 0x2B] << 8));
            info.LoopEndMeasure   = (ushort)(data[dataBase + 0x2C] | (data[dataBase + 0x2D] << 8));
            info.LoopCount        = (ushort)(data[dataBase + 0x2E] | (data[dataBase + 0x2F] << 8));

            for (int i = 0; i < info.ChannelOffsets.Length; i++) {
                int offset = dataBase + i * 2;
                if (offset + 1 >= data.Length) { break; }
                ushort relative = (ushort)(data[offset] | (data[offset + 1] << 8));
                info.ChannelOffsets[i] = relative;
                info.ChannelActive[i]  = relative != 0;
            }
        }

        int active = 0;
        for (int i = 0; i < info.ChannelActive.Length; i++) {
            if (info.ChannelActive[i]) { active++; }
        }
        info.ActiveChannelCount = active;
        headerInfo = info;
        return true;
    }
}

/// <summary>Parsed ADG song header metadata, for display and diagnostics.</summary>
public sealed class SongHeaderInfo {
    /// <summary>Raw file size after any decompression.</summary>
    public int RawFileSize { get; set; }

    /// <summary><see langword="true"/> if the file was HSQ-compressed.</summary>
    public bool WasHsqCompressed { get; set; }

    /// <summary>Byte offset of the channel table (always 2 for ADG).</summary>
    public int DataBase { get; set; }

    /// <summary>Byte offset of the event / instrument bank within the file.</summary>
    public int EventBase { get; set; }

    /// <summary>Song tempo word (driver adds this to the accumulator each tick).</summary>
    public ushort Tempo { get; set; }

    /// <summary>Measure number at which the loop begins.</summary>
    public ushort LoopStartMeasure { get; set; }

    /// <summary>Measure number at which the loop ends.</summary>
    public ushort LoopEndMeasure { get; set; }

    /// <summary>Number of times the loop repeats.</summary>
    public ushort LoopCount { get; set; }

    /// <summary>Number of instrument patches inferred from file length.</summary>
    public int InstrumentCount { get; set; }

    /// <summary>Number of channels with non-zero event offsets.</summary>
    public int ActiveChannelCount { get; set; }

    /// <summary>Relative event-data offsets for each of the 18 channels.</summary>
    public ushort[] ChannelOffsets { get; set; } = new ushort[18];

    /// <summary>Whether each channel has a non-zero (active) offset.</summary>
    public bool[] ChannelActive { get; set; } = new bool[18];
}

/// <summary>Snapshot of a single OPL3 Gold channel's state for UI display.</summary>
public sealed class ChannelSnapshot {
    /// <summary>Zero-based channel index (0–17).</summary>
    public int Channel { get; set; }

    /// <summary>Current wait counter value.</summary>
    public ushort Wait { get; set; }

    /// <summary>Currently loaded instrument index.</summary>
    public byte Instrument { get; set; }

    /// <summary>Current MIDI note number (0 = silent).</summary>
    public byte Note { get; set; }

    /// <summary>Pitch transpose applied to note events.</summary>
    public byte Transpose { get; set; }

    /// <summary>Cached OPL3 frequency word (A0/B0 pair).</summary>
    public ushort Frequency { get; set; }

    /// <summary>Pitch-mode word (Lo8 = mode, Hi8 = transpose seed).</summary>
    public ushort PitchBendFlag { get; set; }

    /// <summary><see langword="true"/> when the channel has an active event pointer.</summary>
    public bool IsActive { get; set; }

    /// <summary>Human-readable note name from the current <see cref="Note"/> value.</summary>
    public string NoteName {
        get {
            if (Note == 0) { return "---"; }
            string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            int octave   = (Note / 12) - 1;
            int semitone = Note % 12;
            return $"{names[semitone]}{octave}";
        }
    }
}
