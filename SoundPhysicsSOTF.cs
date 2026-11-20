using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Construction.Utils;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using RedLoader;
using SonsSdk;
using SonsSdk.Attributes;
using TheForest.Utils;
using UnityEngine;

namespace SoundPhysicsSOTF;

internal enum SurfaceKind
{
    Ignore,
    Terrain,
    Stone,
    Wood,
    Foliage,
    Generic,
    Water
}

internal sealed class TrackedSound
{
    public FMOD.Studio.EventInstance Instance;
    public IntPtr Handle;
    public FMOD.ChannelGroup Group;
    public FMOD.DSP Filter;
    public bool HasFilter;
    public bool FromHook;
    public float NextCheck;
    public float TargetOcclusion;
    public float Occlusion;
    public float SentCutoff = -1f;
    public float SentGain = -1f;
    public float ClearSince;
    public float Distance;
    public int GroupAttempts;
}

internal struct PendingSound
{
    public IntPtr Handle;
    public Vector3 Position;
    public bool HasPosition;
}

internal struct AcousticStats
{
    public float Enclosure;
    public float Overhead;
    public float AverageNear;
    public float AverageAll;
    public float FarFraction;
    public float AverageFar;
    public float HardFraction;
    public float WoodFraction;
    public float FoliageFraction;
    public float GenericFraction;
    public float HitRatio;
}

public class SoundPhysicsMod : SonsMod, IOnUpdateReceiver
{
    private const string LogPrefix = "[SoundPhysicsSOTF] ";
    private const float ProbeInterval = 0.2f;
    private const float OcclusionInterval = 0.15f;
    private const float OcclusionJitter = 0.05f;
    private const int MaxOcclusionChecksPerFrame = 24;
    private const int MaxFilterAttachesPerFrame = 16;
    private const int SweepDescriptionsPerFrame = 160;
    private const int MaxPendingPerFrame = 96;
    private const int MaxTrackedSounds = 320;
    private const int MaxPooledFilters = 64;
    private const int MaxGroupAttempts = 90;
    private const int HitBufferSize = 24;
    private const int SurfaceCacheLimit = 8192;
    private const int SnapTimelineMs = 80;
    private const float ClearCutoff = 22000f;
    private const float SelfSoundRadius = 1.25f;
    private const float OcclusionMaxDistance = 400f;
    private const float OcclusionSmoothing = 7f;
    private const float FilterIdleRelease = 2f;
    private const float BankRefreshInterval = 3f;
    private const float MasterVerifyInterval = 5f;
    private const float BusRetryInterval = 0.5f;
    private const float OverlayRefreshInterval = 0.25f;
    private const float NearHitDistance = 25f;
    private const float OverheadDistance = 40f;
    private const float CanyonMinDistance = 35f;
    private const float SpeedOfSound = 343f;
    private const float SilentDb = -80f;
    private const float MinWetAmplitude = 0.0001f;
    private const float StatsBlend = 0.5f;
    private const int OverheadStart = 12;
    private const int ReverbParamCount = 13;

    private static readonly int PDecay = (int)FMOD.DSP_SFXREVERB.DECAYTIME;
    private static readonly int PEarly = (int)FMOD.DSP_SFXREVERB.EARLYDELAY;
    private static readonly int PLate = (int)FMOD.DSP_SFXREVERB.LATEDELAY;
    private static readonly int PHfRef = (int)FMOD.DSP_SFXREVERB.HFREFERENCE;
    private static readonly int PHfRatio = (int)FMOD.DSP_SFXREVERB.HFDECAYRATIO;
    private static readonly int PDiffusion = (int)FMOD.DSP_SFXREVERB.DIFFUSION;
    private static readonly int PDensity = (int)FMOD.DSP_SFXREVERB.DENSITY;
    private static readonly int PLowShelfFreq = (int)FMOD.DSP_SFXREVERB.LOWSHELFFREQUENCY;
    private static readonly int PLowShelfGain = (int)FMOD.DSP_SFXREVERB.LOWSHELFGAIN;
    private static readonly int PHighCut = (int)FMOD.DSP_SFXREVERB.HIGHCUT;
    private static readonly int PEarlyLateMix = (int)FMOD.DSP_SFXREVERB.EARLYLATEMIX;
    private static readonly int PWet = (int)FMOD.DSP_SFXREVERB.WETLEVEL;
    private static readonly int PDry = (int)FMOD.DSP_SFXREVERB.DRYLEVEL;

    private static readonly float[] ParamMin = { 100f, 0f, 0f, 20f, 10f, 0f, 0f, 20f, -36f, 20f, 0f, -80f, -80f };
    private static readonly float[] ParamMax = { 20000f, 300f, 100f, 20000f, 100f, 100f, 100f, 1000f, 12f, 20000f, 100f, 20f, 20f };
    private static readonly float[] ParamThreshold = { 5f, 0.5f, 0.5f, 25f, 0.5f, 0.5f, 0.5f, 5f, 0.1f, 25f, 0.5f, 0.05f, 0.05f };

    private static readonly string[] ExcludedLayerExact = { "ui", "water", "player", "camera", "pickup", "blocker", "inventory" };
    private static readonly string[] ExcludedLayerTokens = { "player", "camera", "pickup", "impact", "trigger", "inventory", "ragdoll", "character", "hitbox", "weapon", "held", "firstperson", "viewmodel", "transparentfx", "ignore raycast", "particle", "projectile", "sensor", "volume", "postprocess" };
    private static readonly string[] StoneTokens = { "rock", "stone", "cave", "cliff", "boulder", "concrete", "bunker", "cement", "brick", "metal", "steel", "iron", "mountain", "granite", "ore", "tile", "pipe" };
    private static readonly string[] FoliageTokens = { "tree", "bush", "leaf", "leaves", "foliage", "plant", "fern", "shrub", "branch", "pine", "birch", "fir", "cedar", "maple", "sapling", "grass", "canopy" };
    private static readonly string[] WoodTokens = { "wood", "log", "plank", "cabin", "hut", "shack", "wall", "floor", "roof", "beam", "pillar", "structure", "stick", "door", "fence", "house", "deck", "stair", "shelf", "crate", "construction", "building" };

    private static readonly Vector3[] ProbeDirections = new Vector3[16];
    private static readonly Dictionary<IntPtr, TrackedSound> Tracked = new();
    private static readonly List<TrackedSound> TrackedList = new();
    private static readonly List<int> RemovalIndices = new();
    private static readonly ConcurrentQueue<PendingSound> PendingSounds = new();
    private static readonly List<FMOD.Studio.EventDescription> Descriptions = new();
    private static readonly Dictionary<IntPtr, bool> SpatialCache = new();
    private static readonly Dictionary<int, SurfaceKind> SurfaceCache = new();
    private static readonly HashSet<int> UniqueColliders = new();
    private static readonly Stack<FMOD.DSP> FilterPool = new();
    private static readonly float[] CurrentParams = new float[ReverbParamCount];
    private static readonly float[] TargetParams = new float[ReverbParamCount];
    private static readonly float[] SentParams = new float[ReverbParamCount];
    private static readonly float[] CabinParams = new float[ReverbParamCount];
    private static readonly float[] CaveParams = new float[ReverbParamCount];
    private static readonly float[] CanyonParams = new float[ReverbParamCount];
    private static readonly System.Random Rng = new();
    private static readonly StringBuilder OverlayBuilder = new();

    private static Il2CppStructArray<RaycastHit>? _forwardHits;
    private static Il2CppStructArray<RaycastHit>? _reverseHits;

    private static ConfigEntry<bool> _enabled = null!;
    private static ConfigEntry<bool> _reverbEnabled = null!;
    private static ConfigEntry<bool> _occlusionEnabled = null!;
    private static ConfigEntry<float> _reverbIntensity = null!;
    private static ConfigEntry<float> _minCutoff = null!;
    private static ConfigEntry<float> _maxProbeDistance = null!;
    private static ConfigEntry<float> _volumeFloor = null!;
    private static ConfigEntry<bool> _debugOverlay = null!;
    private static ConfigEntry<bool> _useHooks = null!;
    private static ConfigEntry<string> _reverbBusPath = null!;
    private static ConfigCategory _category = null!;

    private static HarmonyLib.Harmony? _harmony;
    private static int _hooksInstalled;
    private static int _hookCaptures;
    private static int _sweepCaptures;

    private static bool _inGame;
    private static bool _listenerApiFailed;
    private static bool _tickErrorLogged;
    private static bool _overlayErrorLogged;
    private static bool _layersLogged;
    private static bool _busesLogged;

    private static FMOD.System _core;
    private static bool _coreValid;
    private static FMOD.DSP_TYPE _filterType = FMOD.DSP_TYPE.LOWPASS;
    private static bool _filterTypeFailed;
    private static int _leakedFilters;

    private static FMOD.DSP _reverbDsp;
    private static FMOD.ChannelGroup _reverbGroup;
    private static bool _reverbAttached;
    private static bool _reverbFailed;
    private static bool _reverbBypassed;
    private static bool _sentValid;
    private static int _reverbAttempts;
    private static int _busAttempts;
    private static string _reverbTargetName = "none";
    private static float _nextReverbVerify;
    private static float _currentWetAmp;
    private static float _targetWetAmp;
    private static float _sentWetDb = float.NaN;

    private static int _occluderMask = Physics.DefaultRaycastLayers;
    private static int _terrainLayer = -1;
    private static int _treeLayer = -1;
    private static int _waterLayer = -1;
    private static int _structureMask;
    private static Transform? _playerRoot;

    private static AcousticStats _stats;
    private static bool _statsValid;
    private static float _weightCave;
    private static float _weightCabin;
    private static float _weightCanyon;
    private static float _weightDry = 1f;
    private static bool _inCaves;
    private static string _environment = "Open Field";

    private static int _bankCount = -1;
    private static int _sweepCursor;
    private static float _nextBankCheck;
    private static float _nextProbe;
    private static float _nextOverlay;
    private static int _occludedCount;
    private static int _filterCount;
    private static string _overlayText = string.Empty;
    private static GUIStyle? _overlayStyle;

    protected override void OnInitializeMod()
    {
        InitializeConfig();
        BuildProbeDirections();
        ResetReverbState();
        if (_useHooks.Value)
        {
            InstallHooks();
        }

        GlobalEvents.OnGUI.Subscribe(DrawOverlay);
        RLog.Msg(LogPrefix + $"Initialized ({_hooksInstalled} play hooks installed).");
    }

    protected override void OnGameStart()
    {
        _inGame = true;
        _playerRoot = null;
        _statsValid = false;
        _nextProbe = 0f;
        _nextBankCheck = 0f;
        _nextReverbVerify = 0f;
        SurfaceCache.Clear();
        BuildOcclusionMask();
    }

    protected override void OnSonsSceneInitialized(ESonsScene sonsScene)
    {
        if (sonsScene == ESonsScene.Game)
        {
            return;
        }

        _inGame = false;
        ReleaseAllSounds();
        SilenceReverb();
        SurfaceCache.Clear();
        _playerRoot = null;
    }

    public void OnUpdate()
    {
        try
        {
            Tick();
        }
        catch (Exception exception)
        {
            if (!_tickErrorLogged)
            {
                _tickErrorLogged = true;
                RLog.Error(LogPrefix + "Update failed: " + exception);
            }
        }
    }

    [DebugCommand("soundphysics")]
    private void SoundPhysicsCommand(string args)
    {
        string option = (args ?? string.Empty).Trim().ToLowerInvariant();
        switch (option)
        {
            case "debug":
                _debugOverlay.Value = !_debugOverlay.Value;
                break;
            case "on":
                _enabled.Value = true;
                break;
            case "off":
                _enabled.Value = false;
                break;
            case "reverb":
                _reverbEnabled.Value = !_reverbEnabled.Value;
                break;
            case "occlusion":
                _occlusionEnabled.Value = !_occlusionEnabled.Value;
                break;
            case "reset":
                ReleaseAllSounds();
                SurfaceCache.Clear();
                BuildOcclusionMask();
                _bankCount = -1;
                _nextBankCheck = 0f;
                break;
            case "save":
                _category.SaveToFile(false);
                break;
            default:
                RLog.Msg(LogPrefix + "Usage: soundphysics debug|on|off|reverb|occlusion|reset|save");
                return;
        }

        RLog.Msg(LogPrefix + $"Enabled={_enabled.Value} Reverb={_reverbEnabled.Value} Occlusion={_occlusionEnabled.Value} Overlay={_debugOverlay.Value}");
    }

    private static void InitializeConfig()
    {
        string fileName = Path.Combine("SoundPhysicsSOTF", "SoundPhysicsSOTF.cfg");
        string? directory = Path.GetDirectoryName(ConfigSystem.GetFilePath(fileName));
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        _category = ConfigSystem.CreateFileCategory("SoundPhysicsSOTF", "Sound Physics", fileName);
        _enabled = _category.CreateEntry("ModEnabled", true, "Mod Enabled", "Master switch for environment reverb and sound occlusion.");
        _reverbEnabled = _category.CreateEntry("ReverbEnabled", true, "Environment Reverb", "Dynamic environment reverb on the master mix.");
        _occlusionEnabled = _category.CreateEntry("OcclusionEnabled", true, "Sound Occlusion", "Muffles sounds behind terrain, walls and rock.");
        _reverbIntensity = _category.CreateEntry("ReverbIntensityMultiplier", 1f, "Reverb Intensity Multiplier", "Scales the reverb wet level. 1 = default, 0 = off, 2 = double.");
        _reverbIntensity.SetRange(0f, 2f);
        _minCutoff = _category.CreateEntry("MinOcclusionCutoffHz", 900f, "Min Occlusion Cutoff Hz", "Low-pass cutoff for a fully occluded sound. Lower = more muffled.");
        _minCutoff.SetRange(300f, 5000f);
        _maxProbeDistance = _category.CreateEntry("MaxProbeDistance", 120f, "Max Probe Distance", "Range in meters of the reverb environment probe rays.");
        _maxProbeDistance.SetRange(20f, 200f);
        _volumeFloor = _category.CreateEntry("OcclusionVolumeFloor", 0.55f, "Occlusion Volume Floor", "Volume multiplier for a fully occluded sound.");
        _volumeFloor.SetRange(0.1f, 1f);
        _debugOverlay = _category.CreateEntry("DebugOverlay", false, "Debug Overlay", "Shows the live acoustic readout on screen.");
        _useHooks = _category.CreateEntry("UsePlayHooks", true, "Use Play Hooks", "Catches one-shot sounds the moment they are played. Requires restart.");
        _reverbBusPath = _category.CreateEntry("ReverbBusPath", string.Empty, "Reverb Bus Path", "Empty = master mix. Otherwise an FMOD bus path such as bus:/SFX (bus paths are listed in the log).");
        _category.LoadFromFile(false);
    }

    private static void BuildProbeDirections()
    {
        int index = 0;
        for (int i = 0; i < 8; i++)
        {
            ProbeDirections[index++] = Direction(i * 45f, 5f);
        }

        for (int i = 0; i < 4; i++)
        {
            ProbeDirections[index++] = Direction(i * 90f + 45f, 40f);
        }

        for (int i = 0; i < 3; i++)
        {
            ProbeDirections[index++] = Direction(i * 120f + 20f, 72f);
        }

        ProbeDirections[index] = Vector3.up;
    }

    private static Vector3 Direction(float yaw, float elevation)
    {
        float y = yaw * Mathf.Deg2Rad;
        float e = elevation * Mathf.Deg2Rad;
        float c = Mathf.Cos(e);
        return new Vector3(c * Mathf.Sin(y), Mathf.Sin(e), c * Mathf.Cos(y)).normalized;
    }

    private static void InstallHooks()
    {
        try
        {
            _harmony = new HarmonyLib.Harmony("Zemo.SoundPhysicsSOTF");
        }
        catch (Exception exception)
        {
            RLog.Warning(LogPrefix + "Harmony unavailable, using the event sweep only: " + exception.Message);
            return;
        }

        _hooksInstalled += PatchMethods(typeof(FMODCommon), "PlayOneshotInternal");
        _hooksInstalled += PatchMethods(typeof(FMODCommon), "PlayOneshot");
        _hooksInstalled += PatchMethods(typeof(FMODCommon), "PlayOneshotNetworked");
        _hooksInstalled += PatchMethods(typeof(FMOD_StudioSystem), "PlayOneShot");
        _hooksInstalled += PatchMethods(typeof(SonsFMODEventEmitter), "Play");
    }

    private static int PatchMethods(Type type, string name)
    {
        int patched = 0;
        MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        foreach (MethodInfo method in methods)
        {
            if (method.Name != name || method.IsGenericMethod || method.IsAbstract)
            {
                continue;
            }

            string? postfix = SelectPostfix(method);
            if (postfix == null)
            {
                continue;
            }

            try
            {
                _harmony!.Patch(method, postfix: new HarmonyMethod(typeof(SoundPhysicsMod), postfix));
                patched++;
            }
            catch (Exception exception)
            {
                RLog.Warning(LogPrefix + $"Could not hook {type.Name}.{name}: {exception.Message}");
            }
        }

        return patched;
    }

    private static string? SelectPostfix(MethodInfo method)
    {
        ParameterInfo[] parameters = method.GetParameters();
        foreach (ParameterInfo parameter in parameters)
        {
            if (parameter.ParameterType.IsArray || parameter.ParameterType.IsByRef)
            {
                return null;
            }
        }

        if (method.ReturnType == typeof(FMOD.Studio.EventInstance))
        {
            foreach (ParameterInfo parameter in parameters)
            {
                if (parameter.Name == "position" && parameter.ParameterType == typeof(Vector3))
                {
                    return nameof(AfterOneshotAtPosition);
                }
            }

            foreach (ParameterInfo parameter in parameters)
            {
                if (parameter.Name == "transform" && parameter.ParameterType == typeof(Transform))
                {
                    return nameof(AfterOneshotOnTransform);
                }
            }

            return null;
        }

        if (method.ReturnType == typeof(void) && method.DeclaringType == typeof(SonsFMODEventEmitter) && parameters.Length == 0 && !method.IsStatic)
        {
            return nameof(AfterEmitterPlay);
        }

        return null;
    }

    private static void AfterOneshotAtPosition(FMOD.Studio.EventInstance __result, Vector3 position)
    {
        try
        {
            if (!_inGame || __result.handle == IntPtr.Zero)
            {
                return;
            }

            PendingSounds.Enqueue(new PendingSound { Handle = __result.handle, Position = position, HasPosition = true });
        }
        catch (Exception)
        {
        }
    }

    private static void AfterOneshotOnTransform(FMOD.Studio.EventInstance __result, Transform transform)
    {
        try
        {
            if (!_inGame || __result.handle == IntPtr.Zero)
            {
                return;
            }

            bool hasPosition = transform != null;
            Vector3 position = hasPosition ? transform!.position : Vector3.zero;
            PendingSounds.Enqueue(new PendingSound { Handle = __result.handle, Position = position, HasPosition = hasPosition });
        }
        catch (Exception)
        {
        }
    }

    private static void AfterEmitterPlay(SonsFMODEventEmitter __instance)
    {
        try
        {
            if (!_inGame || __instance == null)
            {
                return;
            }

            FMOD.Studio.EventInstance instance = __instance.EventInstance;
            if (instance.handle == IntPtr.Zero)
            {
                return;
            }

            Vector3 position = __instance.transform.position;
            PendingSounds.Enqueue(new PendingSound { Handle = instance.handle, Position = position, HasPosition = true });
        }
        catch (Exception)
        {
        }
    }

    private static void Tick()
    {
        float now = Time.unscaledTime;
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);

        if (!_inGame || !TryGetStudio(out FMOD_StudioSystem? studio) || studio == null)
        {
            DrainPending();
            return;
        }

        bool enabled = _enabled.Value;
        bool listenerValid = TryGetListener(out Vector3 listener);
        ResolvePlayerRoot();

        EnsureReverb(studio, now);
        bool reverbActive = enabled && _reverbEnabled.Value && listenerValid;
        if (reverbActive && now >= _nextProbe)
        {
            _nextProbe = now + ProbeInterval;
            RunProbe(listener);
            ComputeReverbTargets();
        }
        else if (!reverbActive)
        {
            _targetWetAmp = 0f;
        }

        ApplyReverb(dt);

        bool occlusionActive = enabled && _occlusionEnabled.Value && listenerValid && _coreValid;
        if (occlusionActive)
        {
            RefreshDescriptions(studio, now);
            ProcessPending(listener, now);
            SweepInstances(listener, now);
        }
        else
        {
            DrainPending();
        }

        UpdateSounds(listener, now, dt, occlusionActive);

        if (_debugOverlay.Value && now >= _nextOverlay)
        {
            _nextOverlay = now + OverlayRefreshInterval;
            BuildOverlayText();
        }
    }

    private static bool TryGetStudio(out FMOD_StudioSystem? studio)
    {
        studio = null;
        if (!FMOD_StudioSystem.TryGetInstance(out FMOD_StudioSystem instance) || instance == null)
        {
            _coreValid = false;
            return false;
        }

        studio = instance;
        _core = instance.CoreSystem;
        _coreValid = _core.handle != IntPtr.Zero;
        return _coreValid;
    }

    private static bool TryGetListener(out Vector3 position)
    {
        position = Vector3.zero;
        if (!_listenerApiFailed)
        {
            try
            {
                position = Assemblies.Sons.FMOD.FMODUtils.GetListenerPosition();
                if (position.sqrMagnitude > 0.0001f)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                _listenerApiFailed = true;
            }
        }

        Transform camera = LocalPlayer.MainCamTr;
        if (camera != null)
        {
            position = camera.position;
            return true;
        }

        return false;
    }

    private static void ResolvePlayerRoot()
    {
        if (_playerRoot != null)
        {
            return;
        }

        try
        {
            Transform player = LocalPlayer.Transform;
            if (player != null)
            {
                _playerRoot = player.root;
            }
        }
        catch (Exception)
        {
        }
    }

    private static bool SafeInCaves()
    {
        try
        {
            return LocalPlayer.IsInCaves;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ResetReverbState()
    {
        CurrentParams[PDecay] = 1000f;
        CurrentParams[PEarly] = 10f;
        CurrentParams[PLate] = 15f;
        CurrentParams[PHfRef] = 5000f;
        CurrentParams[PHfRatio] = 50f;
        CurrentParams[PDiffusion] = 50f;
        CurrentParams[PDensity] = 50f;
        CurrentParams[PLowShelfFreq] = 250f;
        CurrentParams[PLowShelfGain] = 0f;
        CurrentParams[PHighCut] = 8000f;
        CurrentParams[PEarlyLateMix] = 50f;
        CurrentParams[PWet] = SilentDb;
        CurrentParams[PDry] = 0f;
        Array.Copy(CurrentParams, TargetParams, ReverbParamCount);
        _currentWetAmp = 0f;
        _targetWetAmp = 0f;
        _sentValid = false;
    }

    private static void EnsureReverb(FMOD_StudioSystem studio, float now)
    {
        if (_reverbFailed || now < _nextReverbVerify)
        {
            return;
        }

        _nextReverbVerify = now + MasterVerifyInterval;
        if (_reverbAttached)
        {
            if (_reverbGroup.getDSPIndex(_reverbDsp, out int _) == FMOD.RESULT.OK)
            {
                return;
            }

            _reverbAttached = false;
            _reverbDsp.release();
            _reverbDsp = default;
            _sentValid = false;
        }

        if (!TryGetReverbGroup(studio, now, out FMOD.ChannelGroup group, out string targetName))
        {
            return;
        }

        FMOD.RESULT result = _core.createDSPByType(FMOD.DSP_TYPE.SFXREVERB, out FMOD.DSP dsp);
        if (result != FMOD.RESULT.OK)
        {
            RegisterReverbFailure("create SFXREVERB", result);
            return;
        }

        _reverbDsp = dsp;
        _sentValid = false;
        SendReverbParams(true);
        dsp.setBypass(true);
        _reverbBypassed = true;

        result = group.addDSP(FMOD.CHANNELCONTROL_DSP_INDEX.TAIL, dsp);
        if (result != FMOD.RESULT.OK)
        {
            dsp.release();
            _reverbDsp = default;
            RegisterReverbFailure("attach SFXREVERB", result);
            return;
        }

        _reverbGroup = group;
        _reverbAttached = true;
        _reverbTargetName = targetName;
        RLog.Msg(LogPrefix + "Environment reverb attached to " + targetName + ".");
    }

    private static void RegisterReverbFailure(string step, FMOD.RESULT result)
    {
        _reverbAttempts++;
        RLog.Warning(LogPrefix + $"Could not {step} ({result}), attempt {_reverbAttempts}.");
        if (_reverbAttempts >= 5)
        {
            _reverbFailed = true;
            RLog.Warning(LogPrefix + "Environment reverb disabled for this session.");
        }
    }

    private static bool TryGetReverbGroup(FMOD_StudioSystem studio, float now, out FMOD.ChannelGroup group, out string targetName)
    {
        group = default;
        string path = (_reverbBusPath.Value ?? string.Empty).Trim();
        if (path.Length > 0 && _busAttempts < 10)
        {
            FMOD.Studio.System system = studio.System;
            if (system.getBus(path, out FMOD.Studio.Bus bus) == FMOD.RESULT.OK && bus.isValid())
            {
                bus.lockChannelGroup();
                if (bus.getChannelGroup(out group) == FMOD.RESULT.OK && group.handle != IntPtr.Zero)
                {
                    targetName = path;
                    return true;
                }
            }

            _busAttempts++;
            _nextReverbVerify = now + BusRetryInterval;
            if (_busAttempts < 10)
            {
                targetName = string.Empty;
                return false;
            }

            RLog.Warning(LogPrefix + $"Reverb bus '{path}' not available, using the master mix.");
        }

        targetName = "master";
        return _core.getMasterChannelGroup(out group) == FMOD.RESULT.OK && group.handle != IntPtr.Zero;
    }

    private static void SilenceReverb()
    {
        _targetWetAmp = 0f;
        _currentWetAmp = 0f;
        if (!_reverbAttached)
        {
            return;
        }

        _reverbDsp.setParameterFloat(PWet, SilentDb);
        _sentWetDb = SilentDb;
        _reverbDsp.setBypass(true);
        _reverbBypassed = true;
    }

    private static void RunProbe(Vector3 origin)
    {
        float maxDistance = _maxProbeDistance.Value;
        Quaternion jitter = Quaternion.AngleAxis((float)(Rng.NextDouble() * 45.0), Vector3.up);
        int nearHits = 0;
        int anyHits = 0;
        int farHits = 0;
        float nearSum = 0f;
        float allSum = 0f;
        float farSum = 0f;
        float overhead = 0f;
        float hard = 0f;
        float wood = 0f;
        float foliage = 0f;
        float generic = 0f;

        for (int i = 0; i < ProbeDirections.Length; i++)
        {
            Vector3 direction = jitter * ProbeDirections[i];
            if (!Physics.Raycast(origin, direction, out RaycastHit hit, maxDistance, _occluderMask, QueryTriggerInteraction.Ignore))
            {
                continue;
            }

            SurfaceKind kind = Classify(hit);
            if (kind == SurfaceKind.Ignore || kind == SurfaceKind.Water)
            {
                continue;
            }

            float distance = hit.distance;
            anyHits++;
            allSum += distance;

            if (i >= OverheadStart && distance <= OverheadDistance)
            {
                overhead += kind == SurfaceKind.Foliage ? 0.35f : 1f;
            }

            if (distance <= NearHitDistance)
            {
                nearHits++;
                nearSum += distance;
                switch (kind)
                {
                    case SurfaceKind.Terrain:
                    case SurfaceKind.Stone:
                        hard += 1f;
                        break;
                    case SurfaceKind.Wood:
                        wood += 1f;
                        break;
                    case SurfaceKind.Foliage:
                        foliage += 1f;
                        break;
                    default:
                        generic += 1f;
                        break;
                }
            }
            else if (i < OverheadStart && distance >= CanyonMinDistance && (kind == SurfaceKind.Terrain || kind == SurfaceKind.Stone || kind == SurfaceKind.Generic))
            {
                farHits++;
                farSum += distance;
            }
        }

        AcousticStats sample = new()
        {
            Enclosure = nearHits / (float)ProbeDirections.Length,
            Overhead = Mathf.Clamp01(overhead / (ProbeDirections.Length - OverheadStart)),
            AverageNear = nearHits > 0 ? nearSum / nearHits : NearHitDistance,
            AverageAll = anyHits > 0 ? allSum / anyHits : maxDistance,
            FarFraction = farHits / (float)OverheadStart,
            AverageFar = farHits > 0 ? farSum / farHits : (_statsValid ? _stats.AverageFar : CanyonMinDistance),
            HardFraction = nearHits > 0 ? hard / nearHits : 0f,
            WoodFraction = nearHits > 0 ? wood / nearHits : 0f,
            FoliageFraction = nearHits > 0 ? foliage / nearHits : 0f,
            GenericFraction = nearHits > 0 ? generic / nearHits : 0f,
            HitRatio = anyHits / (float)ProbeDirections.Length
        };

        if (!_statsValid)
        {
            _stats = sample;
            _statsValid = true;
            return;
        }

        _stats.Enclosure = Mathf.Lerp(_stats.Enclosure, sample.Enclosure, StatsBlend);
        _stats.Overhead = Mathf.Lerp(_stats.Overhead, sample.Overhead, StatsBlend);
        _stats.AverageNear = Mathf.Lerp(_stats.AverageNear, sample.AverageNear, StatsBlend);
        _stats.AverageAll = Mathf.Lerp(_stats.AverageAll, sample.AverageAll, StatsBlend);
        _stats.FarFraction = Mathf.Lerp(_stats.FarFraction, sample.FarFraction, StatsBlend);
        _stats.AverageFar = Mathf.Lerp(_stats.AverageFar, sample.AverageFar, StatsBlend);
        _stats.HardFraction = Mathf.Lerp(_stats.HardFraction, sample.HardFraction, StatsBlend);
        _stats.WoodFraction = Mathf.Lerp(_stats.WoodFraction, sample.WoodFraction, StatsBlend);
        _stats.FoliageFraction = Mathf.Lerp(_stats.FoliageFraction, sample.FoliageFraction, StatsBlend);
        _stats.GenericFraction = Mathf.Lerp(_stats.GenericFraction, sample.GenericFraction, StatsBlend);
        _stats.HitRatio = Mathf.Lerp(_stats.HitRatio, sample.HitRatio, StatsBlend);
    }

    private static float Smooth01(float value, float from, float to)
    {
        float t = Mathf.Clamp01((value - from) / (to - from));
        return t * t * (3f - 2f * t);
    }

    private static void ComputeReverbTargets()
    {
        _inCaves = SafeInCaves();
        float roofed = Smooth01(_stats.Overhead, 0.25f, 0.75f);
        float hardness = Mathf.Clamp01(_stats.HardFraction + 0.5f * _stats.GenericFraction);
        float woodiness = Mathf.Clamp01(_stats.WoodFraction + 0.5f * _stats.GenericFraction);
        float size = _stats.AverageNear <= 8f ? 1f : Mathf.Lerp(1f, 0.25f, Mathf.InverseLerp(8f, 16f, _stats.AverageNear));

        float cave = Smooth01(_stats.Enclosure, 0.55f, 0.9f) * roofed * hardness;
        float cabin = Smooth01(_stats.Enclosure, 0.45f, 0.8f) * roofed * woodiness * size;
        float canyon = (1f - roofed) * Smooth01(_stats.FarFraction, 0.08f, 0.35f) * (1f - 0.7f * Smooth01(_stats.FoliageFraction, 0.3f, 0.8f));

        if (_inCaves)
        {
            cave = Mathf.Max(cave, 0.9f);
            cabin *= 0.3f;
            canyon = 0f;
        }

        float total = cave + cabin + canyon;
        if (total > 1f)
        {
            cave /= total;
            cabin /= total;
            canyon /= total;
            total = 1f;
        }

        _weightCave = cave;
        _weightCabin = cabin;
        _weightCanyon = canyon;
        _weightDry = 1f - total;

        if (_weightDry >= cave && _weightDry >= cabin && _weightDry >= canyon)
        {
            _environment = _stats.FoliageFraction > 0.4f && _stats.Enclosure > 0.25f ? "Dense Forest" : "Open Field";
        }
        else if (cave >= cabin && cave >= canyon)
        {
            _environment = _inCaves ? "Cave" : "Cave / Stone Bunker";
        }
        else if (cabin >= canyon)
        {
            _environment = "Cabin / Tight Interior";
        }
        else
        {
            _environment = "Canyon / Cliff Echo";
        }

        FillCabin(CabinParams, _stats.AverageNear);
        FillCave(CaveParams, _stats.AverageNear);
        FillCanyon(CanyonParams, _stats.AverageFar, _stats.FarFraction);

        if (total > 0.001f)
        {
            for (int i = 0; i < ReverbParamCount; i++)
            {
                if (i == PWet || i == PDry)
                {
                    continue;
                }

                TargetParams[i] = (cave * CaveParams[i] + cabin * CabinParams[i] + canyon * CanyonParams[i]) / total;
            }
        }

        TargetParams[PDry] = 0f;
        float wetAmp = cave * DbToAmp(CaveParams[PWet]) + cabin * DbToAmp(CabinParams[PWet]) + canyon * DbToAmp(CanyonParams[PWet]);
        _targetWetAmp = Mathf.Min(wetAmp * _reverbIntensity.Value, 0.8f);
    }

    private static void FillCabin(float[] p, float averageNear)
    {
        float t = Mathf.InverseLerp(2f, 8f, averageNear);
        float early = Mathf.Clamp(averageNear / SpeedOfSound * 1000f, 2f, 25f);
        p[PDecay] = Mathf.Lerp(400f, 800f, t);
        p[PEarly] = early;
        p[PLate] = Mathf.Clamp(early * 1.6f, 4f, 40f);
        p[PHfRef] = 5000f;
        p[PHfRatio] = 45f;
        p[PDiffusion] = 95f;
        p[PDensity] = 100f;
        p[PLowShelfFreq] = 250f;
        p[PLowShelfGain] = 0f;
        p[PHighCut] = 6000f;
        p[PEarlyLateMix] = 60f;
        p[PWet] = -9f;
        p[PDry] = 0f;
    }

    private static void FillCave(float[] p, float averageNear)
    {
        float t = Mathf.InverseLerp(3f, 25f, averageNear);
        float early = Mathf.Clamp(averageNear / SpeedOfSound * 1000f, 5f, 90f);
        p[PDecay] = Mathf.Lerp(2000f, 3800f, t);
        p[PEarly] = early;
        p[PLate] = Mathf.Clamp(early * 1.5f, 10f, 100f);
        p[PHfRef] = 5000f;
        p[PHfRatio] = 85f;
        p[PDiffusion] = 100f;
        p[PDensity] = 100f;
        p[PLowShelfFreq] = 250f;
        p[PLowShelfGain] = 0f;
        p[PHighCut] = 14000f;
        p[PEarlyLateMix] = 40f;
        p[PWet] = -5f;
        p[PDry] = 0f;
    }

    private static void FillCanyon(float[] p, float averageFar, float farFraction)
    {
        float early = Mathf.Clamp(averageFar / SpeedOfSound * 1000f, 20f, 300f);
        p[PDecay] = Mathf.Lerp(1200f, 2200f, Mathf.InverseLerp(CanyonMinDistance, 120f, averageFar));
        p[PEarly] = early;
        p[PLate] = Mathf.Clamp(early * 0.25f + 15f, 15f, 100f);
        p[PHfRef] = 4000f;
        p[PHfRatio] = 55f;
        p[PDiffusion] = 25f;
        p[PDensity] = 35f;
        p[PLowShelfFreq] = 250f;
        p[PLowShelfGain] = 0f;
        p[PHighCut] = 4500f;
        p[PEarlyLateMix] = 80f;
        p[PWet] = Mathf.Lerp(-16f, -12f, Smooth01(farFraction, 0.1f, 0.5f));
        p[PDry] = 0f;
    }

    private static float DbToAmp(float db)
    {
        return db <= SilentDb ? 0f : Mathf.Pow(10f, db / 20f);
    }

    private static float AmpToDb(float amp)
    {
        return amp <= MinWetAmplitude ? SilentDb : Mathf.Max(SilentDb, 20f * Mathf.Log10(amp));
    }

    private static void ApplyReverb(float dt)
    {
        float shapeRate = 1f - Mathf.Exp(-dt * 2f);
        float delayRate = 1f - Mathf.Exp(-dt * 1.2f);
        float wetRate = 1f - Mathf.Exp(-dt * 2.5f);

        for (int i = 0; i < ReverbParamCount; i++)
        {
            if (i == PWet || i == PDry)
            {
                continue;
            }

            float rate = i == PEarly || i == PLate ? delayRate : shapeRate;
            CurrentParams[i] = Mathf.Lerp(CurrentParams[i], TargetParams[i], rate);
        }

        _currentWetAmp = Mathf.Lerp(_currentWetAmp, _targetWetAmp, wetRate);
        if (_targetWetAmp <= MinWetAmplitude && _currentWetAmp < MinWetAmplitude * 2f)
        {
            _currentWetAmp = 0f;
        }

        CurrentParams[PWet] = AmpToDb(_currentWetAmp);
        CurrentParams[PDry] = 0f;

        if (!_reverbAttached)
        {
            return;
        }

        bool silent = _currentWetAmp <= 0f && _targetWetAmp <= MinWetAmplitude;
        if (silent)
        {
            if (!_reverbBypassed)
            {
                _reverbDsp.setParameterFloat(PWet, SilentDb);
                _sentWetDb = SilentDb;
                _reverbDsp.setBypass(true);
                _reverbBypassed = true;
            }

            return;
        }

        SendReverbParams(false);
        if (_reverbBypassed)
        {
            _reverbDsp.reset();
            _reverbDsp.setBypass(false);
            _reverbBypassed = false;
        }
    }

    private static void SendReverbParams(bool force)
    {
        for (int i = 0; i < ReverbParamCount; i++)
        {
            float value = Mathf.Clamp(CurrentParams[i], ParamMin[i], ParamMax[i]);
            if (i == PWet)
            {
                if (force || float.IsNaN(_sentWetDb) || Mathf.Abs(value - _sentWetDb) > ParamThreshold[i])
                {
                    _reverbDsp.setParameterFloat(i, value);
                    _sentWetDb = value;
                }

                continue;
            }

            if (force || !_sentValid || Mathf.Abs(value - SentParams[i]) > ParamThreshold[i])
            {
                _reverbDsp.setParameterFloat(i, value);
                SentParams[i] = value;
            }
        }

        _sentValid = true;
    }

    private static void BuildOcclusionMask()
    {
        int excluded = 0;
        excluded |= LayerBit(() => Layers.Player);
        excluded |= LayerBit(() => Layers.Camera);
        excluded |= LayerBit(() => Layers.PickUp);
        excluded |= LayerBit(() => Layers.ImpactDetect);
        excluded |= LayerBit(() => Layers.Water);
        excluded |= LayerBit(() => Layers.Blocker);
        excluded |= SafeInt(() => Layers.PlayerAndCharacterMask, 0);

        StringBuilder table = new();
        for (int layer = 0; layer < 32; layer++)
        {
            string name;
            try
            {
                name = LayerMask.LayerToName(layer) ?? string.Empty;
            }
            catch (Exception)
            {
                name = string.Empty;
            }

            if (name.Length == 0)
            {
                continue;
            }

            string lower = name.ToLowerInvariant();
            bool skip = Array.IndexOf(ExcludedLayerExact, lower) >= 0 || ContainsAny(lower, ExcludedLayerTokens);
            if (skip)
            {
                excluded |= 1 << layer;
            }

            table.Append(layer).Append('=').Append(name).Append((excluded & (1 << layer)) != 0 ? "(x) " : " ");
        }

        _occluderMask = Physics.DefaultRaycastLayers & ~excluded;
        _terrainLayer = SafeInt(() => Layers.Terrain, -1);
        _treeLayer = SafeInt(() => Layers.TreeWS, -1);
        _waterLayer = SafeInt(() => Layers.Water, -1);
        _structureMask = SafeInt(() => Layers.ScrewStructuresMask, 0) | SafeInt(() => Layers.FreeFormAndScrewStructuresMask, 0);
        _forwardHits ??= new Il2CppStructArray<RaycastHit>(HitBufferSize);
        _reverseHits ??= new Il2CppStructArray<RaycastHit>(HitBufferSize);

        if (!_layersLogged)
        {
            _layersLogged = true;
            RLog.Msg(LogPrefix + $"Layers: {table}");
            RLog.Msg(LogPrefix + $"Occluder mask 0x{_occluderMask:X8}, terrain={_terrainLayer}, trees={_treeLayer}, structures=0x{_structureMask:X8}");
        }
    }

    private static int LayerBit(Func<int> getter)
    {
        int layer = SafeInt(getter, -1);
        return layer >= 0 && layer < 32 ? 1 << layer : 0;
    }

    private static int SafeInt(Func<int> getter, int fallback)
    {
        try
        {
            return getter();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static bool ContainsAny(string value, string[] tokens)
    {
        foreach (string token in tokens)
        {
            if (value.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static SurfaceKind Classify(RaycastHit hit)
    {
        int id = hit.colliderInstanceID;
        if (SurfaceCache.TryGetValue(id, out SurfaceKind cached))
        {
            return cached;
        }

        SurfaceKind kind;
        try
        {
            kind = ClassifyCollider(hit.collider);
        }
        catch (Exception)
        {
            kind = SurfaceKind.Generic;
        }

        if (SurfaceCache.Count >= SurfaceCacheLimit)
        {
            SurfaceCache.Clear();
        }

        SurfaceCache[id] = kind;
        return kind;
    }

    private static SurfaceKind ClassifyCollider(Collider collider)
    {
        if (collider == null)
        {
            return SurfaceKind.Generic;
        }

        Transform transform = collider.transform;
        if (_playerRoot != null && transform.root == _playerRoot)
        {
            return SurfaceKind.Ignore;
        }

        Rigidbody body = collider.attachedRigidbody;
        if (body != null && !body.isKinematic)
        {
            return SurfaceKind.Ignore;
        }

        GameObject gameObject = collider.gameObject;
        int layer = gameObject.layer;
        if (layer == _terrainLayer || collider.TryCast<TerrainCollider>() != null)
        {
            return SurfaceKind.Terrain;
        }

        if (layer == _waterLayer)
        {
            return SurfaceKind.Water;
        }

        if (layer == _treeLayer)
        {
            return SurfaceKind.Foliage;
        }

        if (_structureMask != 0 && (_structureMask & (1 << layer)) != 0)
        {
            return SurfaceKind.Wood;
        }

        StringBuilder key = new(gameObject.name ?? string.Empty);
        Transform parent = transform.parent;
        if (parent != null)
        {
            key.Append('|').Append(parent.name);
        }

        var material = collider.sharedMaterial;
        if (material != null)
        {
            key.Append('|').Append(material.name);
        }

        string lower = key.ToString().ToLowerInvariant();
        if (ContainsAny(lower, StoneTokens))
        {
            return SurfaceKind.Stone;
        }

        if (ContainsAny(lower, FoliageTokens))
        {
            return SurfaceKind.Foliage;
        }

        if (ContainsAny(lower, WoodTokens))
        {
            return SurfaceKind.Wood;
        }

        return SurfaceKind.Generic;
    }

    private static float SurfaceWeight(SurfaceKind kind)
    {
        return kind switch
        {
            SurfaceKind.Terrain => 1f,
            SurfaceKind.Stone => 0.85f,
            SurfaceKind.Wood => 0.6f,
            SurfaceKind.Generic => 0.5f,
            SurfaceKind.Foliage => 0.1f,
            _ => 0f
        };
    }

    private static float MeasureOcclusion(Vector3 listener, Vector3 source, out float distance)
    {
        Vector3 delta = source - listener;
        distance = delta.magnitude;
        if (distance < SelfSoundRadius || distance > OcclusionMaxDistance || _forwardHits == null || _reverseHits == null)
        {
            return 0f;
        }

        Vector3 direction = delta / distance;
        float endTrim = Mathf.Min(0.6f, distance * 0.2f);
        float length = distance - endTrim;
        Vector3 end = listener + direction * length;

        int forwardCount = Physics.RaycastNonAlloc(listener, direction, _forwardHits, length, _occluderMask, QueryTriggerInteraction.Ignore);
        int reverseCount = Physics.RaycastNonAlloc(end, -direction, _reverseHits, length, _occluderMask, QueryTriggerInteraction.Ignore);
        UniqueColliders.Clear();
        float score = 0f;

        for (int i = 0; i < forwardCount && i < HitBufferSize; i++)
        {
            RaycastHit hit = _forwardHits[i];
            if (hit.distance < 0.2f)
            {
                continue;
            }

            int id = hit.colliderInstanceID;
            if (!UniqueColliders.Add(id))
            {
                continue;
            }

            SurfaceKind kind = Classify(hit);
            float weight = SurfaceWeight(kind);
            if (weight <= 0f)
            {
                continue;
            }

            float thickness = -1f;
            for (int j = 0; j < reverseCount && j < HitBufferSize; j++)
            {
                RaycastHit back = _reverseHits[j];
                if (back.colliderInstanceID == id)
                {
                    thickness = Mathf.Max(0f, length - back.distance - hit.distance);
                    break;
                }
            }

            float thick = thickness < 0f ? (kind == SurfaceKind.Terrain ? 1f : 0.25f) : Mathf.Clamp01(thickness / 3f);
            score += weight * (1f + 0.6f * thick);
        }

        for (int j = 0; j < reverseCount && j < HitBufferSize; j++)
        {
            RaycastHit back = _reverseHits[j];
            if (length - back.distance < 0.2f || !UniqueColliders.Add(back.colliderInstanceID))
            {
                continue;
            }

            SurfaceKind kind = Classify(back);
            float weight = SurfaceWeight(kind);
            if (weight > 0f)
            {
                score += weight * (1f + 0.6f * (kind == SurfaceKind.Terrain ? 1f : 0.25f));
            }
        }

        if (score <= 0f)
        {
            return 0f;
        }

        float occlusion = Mathf.Clamp01(score / 1.6f);
        Vector3 side = Vector3.Cross(direction, Vector3.up);
        if (side.sqrMagnitude < 0.0001f)
        {
            side = Vector3.right;
        }

        side.Normalize();
        Vector3 up = Vector3.Cross(side, direction).normalized;
        float spread = Mathf.Clamp(0.04f * distance + 0.4f, 0.4f, 2.5f);
        int clear = 0;
        if (!Physics.Linecast(listener, end + side * spread, _occluderMask, QueryTriggerInteraction.Ignore))
        {
            clear++;
        }

        if (!Physics.Linecast(listener, end + up * spread, _occluderMask, QueryTriggerInteraction.Ignore))
        {
            clear++;
        }

        return occlusion * (1f - 0.22f * clear);
    }

    private static void RefreshDescriptions(FMOD_StudioSystem studio, float now)
    {
        if (now < _nextBankCheck)
        {
            return;
        }

        _nextBankCheck = now + BankRefreshInterval;
        FMOD.Studio.System system = studio.System;
        if (!system.isValid() || system.getBankCount(out int count) != FMOD.RESULT.OK)
        {
            return;
        }

        if (count == _bankCount && Descriptions.Count > 0)
        {
            return;
        }

        if (system.getBankList(out Il2CppStructArray<FMOD.Studio.Bank> banks) != FMOD.RESULT.OK || banks == null)
        {
            return;
        }

        Descriptions.Clear();
        SpatialCache.Clear();
        _sweepCursor = 0;
        StringBuilder? busList = _busesLogged ? null : new StringBuilder();

        for (int b = 0; b < banks.Length; b++)
        {
            FMOD.Studio.Bank bank = banks[b];
            if (!bank.isValid())
            {
                continue;
            }

            if (bank.getEventList(out Il2CppStructArray<FMOD.Studio.EventDescription> events) == FMOD.RESULT.OK && events != null)
            {
                for (int e = 0; e < events.Length; e++)
                {
                    FMOD.Studio.EventDescription description = events[e];
                    if (description.handle == IntPtr.Zero || SpatialCache.ContainsKey(description.handle))
                    {
                        continue;
                    }

                    bool spatial = description.is3D(out bool is3D) == FMOD.RESULT.OK && is3D;
                    SpatialCache[description.handle] = spatial;
                    if (spatial)
                    {
                        Descriptions.Add(description);
                    }
                }
            }

            if (busList != null && bank.getBusList(out Il2CppStructArray<FMOD.Studio.Bus> buses) == FMOD.RESULT.OK && buses != null)
            {
                for (int i = 0; i < buses.Length; i++)
                {
                    FMOD.Studio.Bus bus = buses[i];
                    if (bus.getPath(out string path) == FMOD.RESULT.OK && !string.IsNullOrEmpty(path))
                    {
                        busList.Append(path).Append("  ");
                    }
                }
            }
        }

        _bankCount = count;
        RLog.Msg(LogPrefix + $"Tracking {Descriptions.Count} 3D events across {count} banks.");
        if (busList != null)
        {
            _busesLogged = true;
            RLog.Msg(LogPrefix + "FMOD buses: " + (busList.Length > 0 ? busList.ToString() : "(none reported)"));
        }
    }

    private static void DrainPending()
    {
        while (PendingSounds.TryDequeue(out PendingSound _))
        {
        }
    }

    private static void ProcessPending(Vector3 listener, float now)
    {
        int processed = 0;
        while (processed < MaxPendingPerFrame && PendingSounds.TryDequeue(out PendingSound pending))
        {
            processed++;
            if (Tracked.TryGetValue(pending.Handle, out TrackedSound? existing))
            {
                existing.FromHook = true;
                continue;
            }

            FMOD.Studio.EventInstance instance = new() { handle = pending.Handle };
            if (!instance.isValid() || !IsSpatial(instance))
            {
                continue;
            }

            if (Track(instance, listener, now, true, pending.HasPosition, pending.Position, true))
            {
                _hookCaptures++;
            }
        }
    }

    private static bool IsSpatial(FMOD.Studio.EventInstance instance)
    {
        if (instance.getDescription(out FMOD.Studio.EventDescription description) != FMOD.RESULT.OK || description.handle == IntPtr.Zero)
        {
            return false;
        }

        if (SpatialCache.TryGetValue(description.handle, out bool spatial))
        {
            return spatial;
        }

        spatial = description.is3D(out bool is3D) == FMOD.RESULT.OK && is3D;
        SpatialCache[description.handle] = spatial;
        return spatial;
    }

    private static void SweepInstances(Vector3 listener, float now)
    {
        int count = Descriptions.Count;
        if (count == 0)
        {
            return;
        }

        int budget = Math.Min(SweepDescriptionsPerFrame, count);
        for (int n = 0; n < budget; n++)
        {
            _sweepCursor = (_sweepCursor + 1) % count;
            FMOD.Studio.EventDescription description = Descriptions[_sweepCursor];
            if (description.getInstanceCount(out int instanceCount) != FMOD.RESULT.OK || instanceCount <= 0)
            {
                continue;
            }

            if (description.getInstanceList(out Il2CppStructArray<FMOD.Studio.EventInstance> instances) != FMOD.RESULT.OK || instances == null)
            {
                continue;
            }

            for (int i = 0; i < instances.Length; i++)
            {
                FMOD.Studio.EventInstance instance = instances[i];
                if (instance.handle == IntPtr.Zero || Tracked.ContainsKey(instance.handle) || !IsActive(instance))
                {
                    continue;
                }

                bool snap = instance.getTimelinePosition(out int position) == FMOD.RESULT.OK && position <= SnapTimelineMs;
                if (Track(instance, listener, now, false, false, Vector3.zero, snap))
                {
                    _sweepCaptures++;
                }
            }
        }
    }

    private static bool IsActive(FMOD.Studio.EventInstance instance)
    {
        if (!instance.isValid() || instance.getPlaybackState(out FMOD.Studio.PLAYBACK_STATE state) != FMOD.RESULT.OK)
        {
            return false;
        }

        return state != FMOD.Studio.PLAYBACK_STATE.STOPPED;
    }

    private static bool TryGetPosition(FMOD.Studio.EventInstance instance, out Vector3 position)
    {
        if (instance.get3DAttributes(out FMOD.ATTRIBUTES_3D attributes) == FMOD.RESULT.OK)
        {
            position = new Vector3(attributes.position.x, attributes.position.y, attributes.position.z);
            return true;
        }

        position = Vector3.zero;
        return false;
    }

    private static bool Track(FMOD.Studio.EventInstance instance, Vector3 listener, float now, bool fromHook, bool hasPosition, Vector3 hintPosition, bool snap)
    {
        if (TrackedList.Count >= MaxTrackedSounds || !IsActive(instance))
        {
            return false;
        }

        TrackedSound sound = new()
        {
            Instance = instance,
            Handle = instance.handle,
            FromHook = fromHook,
            NextCheck = now + OcclusionInterval + (float)(Rng.NextDouble() * OcclusionJitter)
        };

        Vector3 position = hintPosition;
        bool known = TryGetPosition(instance, out Vector3 attributePosition) && attributePosition.sqrMagnitude > 0.0001f;
        if (known)
        {
            position = attributePosition;
        }

        if (known || hasPosition)
        {
            sound.TargetOcclusion = MeasureOcclusion(listener, position, out sound.Distance);
            if (snap)
            {
                sound.Occlusion = sound.TargetOcclusion;
            }
        }

        Tracked[sound.Handle] = sound;
        TrackedList.Add(sound);
        if (sound.TargetOcclusion > 0.02f)
        {
            TryAttachFilter(sound);
        }

        return true;
    }

    private static void UpdateSounds(Vector3 listener, float now, float dt, bool occlusionActive)
    {
        int count = TrackedList.Count;
        _occludedCount = 0;
        _filterCount = 0;
        if (count == 0)
        {
            return;
        }

        float rate = 1f - Mathf.Exp(-dt * OcclusionSmoothing);
        float minCutoff = Mathf.Clamp(_minCutoff.Value, 100f, ClearCutoff);
        float floor = Mathf.Clamp01(_volumeFloor.Value);
        int checks = 0;
        int attaches = 0;
        RemovalIndices.Clear();
        int start = (int)(Rng.NextDouble() * count);

        for (int n = 0; n < count; n++)
        {
            int index = (start + n) % count;
            TrackedSound sound = TrackedList[index];

            if (now >= sound.NextCheck && checks < MaxOcclusionChecksPerFrame)
            {
                checks++;
                sound.NextCheck = now + OcclusionInterval + (float)(Rng.NextDouble() * OcclusionJitter);
                if (!IsActive(sound.Instance))
                {
                    RemovalIndices.Add(index);
                    continue;
                }

                if (occlusionActive && TryGetPosition(sound.Instance, out Vector3 position))
                {
                    sound.TargetOcclusion = MeasureOcclusion(listener, position, out sound.Distance);
                }
                else
                {
                    sound.TargetOcclusion = 0f;
                }
            }

            if (!occlusionActive)
            {
                sound.TargetOcclusion = 0f;
            }

            sound.Occlusion = Mathf.Lerp(sound.Occlusion, sound.TargetOcclusion, rate);
            if (sound.Occlusion > 0.05f)
            {
                _occludedCount++;
            }

            if (!sound.HasFilter)
            {
                if (sound.TargetOcclusion > 0.02f && attaches < MaxFilterAttachesPerFrame)
                {
                    attaches++;
                    TryAttachFilter(sound);
                }
            }

            if (!sound.HasFilter)
            {
                continue;
            }

            _filterCount++;
            ApplyFilter(sound, minCutoff, floor);

            if (sound.TargetOcclusion < 0.005f && sound.Occlusion < 0.005f)
            {
                if (sound.ClearSince <= 0f)
                {
                    sound.ClearSince = now;
                }
                else if (now - sound.ClearSince > FilterIdleRelease)
                {
                    DetachFilter(sound);
                }
            }
            else
            {
                sound.ClearSince = 0f;
            }
        }

        if (RemovalIndices.Count == 0)
        {
            return;
        }

        RemovalIndices.Sort();
        for (int i = RemovalIndices.Count - 1; i >= 0; i--)
        {
            int index = RemovalIndices[i];
            TrackedSound sound = TrackedList[index];
            if (sound.HasFilter)
            {
                DetachFilter(sound);
            }

            Tracked.Remove(sound.Handle);
            int last = TrackedList.Count - 1;
            TrackedList[index] = TrackedList[last];
            TrackedList.RemoveAt(last);
        }
    }

    private static float CutoffFor(float occlusion, float minCutoff)
    {
        float logClear = Mathf.Log(ClearCutoff);
        float logMin = Mathf.Log(minCutoff);
        return Mathf.Exp(Mathf.Lerp(logClear, logMin, Mathf.Clamp01(occlusion)));
    }

    private static void ApplyFilter(TrackedSound sound, float minCutoff, float floor)
    {
        float cutoff = CutoffFor(sound.Occlusion, minCutoff);
        float gain = Mathf.Lerp(1f, floor, Mathf.Clamp01(sound.Occlusion));
        if (sound.SentCutoff < 0f || Mathf.Abs(cutoff - sound.SentCutoff) > sound.SentCutoff * 0.01f)
        {
            sound.Filter.setParameterFloat((int)FMOD.DSP_LOWPASS.CUTOFF, cutoff);
            sound.SentCutoff = cutoff;
        }

        if (sound.SentGain < 0f || Mathf.Abs(gain - sound.SentGain) > 0.005f)
        {
            sound.Filter.setWetDryMix(1f, gain, 0f);
            sound.SentGain = gain;
        }
    }

    private static void TryAttachFilter(TrackedSound sound)
    {
        if (sound.HasFilter || sound.GroupAttempts >= MaxGroupAttempts)
        {
            return;
        }

        sound.GroupAttempts++;
        if (sound.Instance.getChannelGroup(out FMOD.ChannelGroup group) != FMOD.RESULT.OK || group.handle == IntPtr.Zero)
        {
            return;
        }

        if (!TryRentFilter(out FMOD.DSP filter))
        {
            return;
        }

        float minCutoff = Mathf.Clamp(_minCutoff.Value, 100f, ClearCutoff);
        float cutoff = CutoffFor(sound.Occlusion, minCutoff);
        float gain = Mathf.Lerp(1f, Mathf.Clamp01(_volumeFloor.Value), Mathf.Clamp01(sound.Occlusion));
        filter.setParameterFloat((int)FMOD.DSP_LOWPASS.CUTOFF, cutoff);
        filter.setWetDryMix(1f, gain, 0f);

        if (group.addDSP(FMOD.CHANNELCONTROL_DSP_INDEX.HEAD, filter) != FMOD.RESULT.OK)
        {
            ReturnFilter(filter);
            return;
        }

        sound.Group = group;
        sound.Filter = filter;
        sound.HasFilter = true;
        sound.SentCutoff = cutoff;
        sound.SentGain = gain;
        sound.ClearSince = 0f;
    }

    private static bool TryRentFilter(out FMOD.DSP filter)
    {
        while (FilterPool.Count > 0)
        {
            filter = FilterPool.Pop();
            if (filter.reset() == FMOD.RESULT.OK)
            {
                return true;
            }
        }

        filter = default;
        if (!_coreValid || _filterTypeFailed)
        {
            return false;
        }

        FMOD.RESULT result = _core.createDSPByType(_filterType, out filter);
        if (result == FMOD.RESULT.OK)
        {
            return true;
        }

        if (_filterType == FMOD.DSP_TYPE.LOWPASS)
        {
            RLog.Warning(LogPrefix + $"LOWPASS DSP unavailable ({result}), falling back to LOWPASS_SIMPLE.");
            _filterType = FMOD.DSP_TYPE.LOWPASS_SIMPLE;
            return _core.createDSPByType(_filterType, out filter) == FMOD.RESULT.OK;
        }

        _filterTypeFailed = true;
        RLog.Warning(LogPrefix + $"Low-pass DSP unavailable ({result}), occlusion filtering disabled.");
        return false;
    }

    private static void ReturnFilter(FMOD.DSP filter)
    {
        if (filter.handle == IntPtr.Zero)
        {
            return;
        }

        filter.setBypass(false);
        if (FilterPool.Count < MaxPooledFilters)
        {
            FilterPool.Push(filter);
        }
        else
        {
            filter.release();
        }
    }

    private static void DetachFilter(TrackedSound sound)
    {
        if (!sound.HasFilter)
        {
            return;
        }

        FMOD.RESULT removed = sound.Group.removeDSP(sound.Filter);
        bool groupAlive = sound.Group.getNumDSPs(out int _) == FMOD.RESULT.OK;
        if (removed == FMOD.RESULT.OK || !groupAlive)
        {
            sound.Filter.disconnectAll(true, true);
            ReturnFilter(sound.Filter);
        }
        else if (sound.Filter.release() != FMOD.RESULT.OK)
        {
            _leakedFilters++;
        }

        sound.HasFilter = false;
        sound.Filter = default;
        sound.Group = default;
        sound.SentCutoff = -1f;
        sound.SentGain = -1f;
        sound.ClearSince = 0f;
        sound.GroupAttempts = 0;
    }

    private static void ReleaseAllSounds()
    {
        foreach (TrackedSound sound in TrackedList)
        {
            try
            {
                DetachFilter(sound);
            }
            catch (Exception)
            {
            }
        }

        TrackedList.Clear();
        Tracked.Clear();
        DrainPending();
    }

    private static void BuildOverlayText()
    {
        StringBuilder sb = OverlayBuilder;
        sb.Clear();
        sb.Append("SoundPhysicsSOTF").Append(_enabled.Value ? "" : "  [DISABLED]").Append('\n');
        sb.Append("Environment: ").Append(_environment).Append(_inCaves ? "  (in caves)" : "").Append('\n');
        sb.Append("Weights  cave ").Append(_weightCave.ToString("0.00"))
          .Append("  cabin ").Append(_weightCabin.ToString("0.00"))
          .Append("  canyon ").Append(_weightCanyon.ToString("0.00"))
          .Append("  dry ").Append(_weightDry.ToString("0.00")).Append('\n');
        sb.Append("Avg ray dist ").Append(_stats.AverageAll.ToString("0.0")).Append("m  near ").Append(_stats.AverageNear.ToString("0.0"))
          .Append("m  far ").Append(_stats.AverageFar.ToString("0.0")).Append("m\n");
        sb.Append("Hit ratio ").Append(_stats.HitRatio.ToString("0.00"))
          .Append("  enclosure ").Append(_stats.Enclosure.ToString("0.00"))
          .Append("  overhead ").Append(_stats.Overhead.ToString("0.00"))
          .Append("  far ").Append(_stats.FarFraction.ToString("0.00")).Append('\n');
        sb.Append("Surfaces  hard ").Append(_stats.HardFraction.ToString("0.00"))
          .Append("  wood ").Append(_stats.WoodFraction.ToString("0.00"))
          .Append("  foliage ").Append(_stats.FoliageFraction.ToString("0.00"))
          .Append("  other ").Append(_stats.GenericFraction.ToString("0.00")).Append('\n');
        sb.Append("Reverb wet ").Append(CurrentParams[PWet].ToString("0.0")).Append(" dB  decay ").Append((CurrentParams[PDecay] / 1000f).ToString("0.00"))
          .Append("s  early ").Append(CurrentParams[PEarly].ToString("0")).Append("ms  late ").Append(CurrentParams[PLate].ToString("0")).Append("ms")
          .Append(_reverbAttached ? (_reverbBypassed ? "  (idle)" : "") : "  (not attached)").Append('\n');
        sb.Append("Reverb target: ").Append(_reverbTargetName).Append("  intensity x").Append(_reverbIntensity.Value.ToString("0.00")).Append('\n');
        sb.Append("Sounds tracked ").Append(TrackedList.Count)
          .Append("  occluded ").Append(_occludedCount)
          .Append("  filters ").Append(_filterCount)
          .Append("  pooled ").Append(FilterPool.Count).Append('\n');
        sb.Append("3D events ").Append(Descriptions.Count)
          .Append("  hooks ").Append(_hooksInstalled)
          .Append("  via hook ").Append(_hookCaptures)
          .Append("  via sweep ").Append(_sweepCaptures);
        if (_leakedFilters > 0)
        {
            sb.Append("  leaked ").Append(_leakedFilters);
        }

        _overlayText = sb.ToString();
    }

    private static void DrawOverlay()
    {
        if (!_inGame || !_debugOverlay.Value || _overlayText.Length == 0)
        {
            return;
        }

        try
        {
            if (_overlayStyle == null)
            {
                _overlayStyle = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 14,
                    wordWrap = false,
                    alignment = TextAnchor.UpperLeft
                };
                _overlayStyle.normal.textColor = new Color(0.85f, 0.95f, 1f, 1f);
            }

            GUI.Box(new Rect(16f, 16f, 560f, 214f), _overlayText, _overlayStyle);
        }
        catch (Exception exception)
        {
            if (!_overlayErrorLogged)
            {
                _overlayErrorLogged = true;
                RLog.Warning(LogPrefix + "Could not draw debug overlay: " + exception.Message);
            }
        }
    }
}
