using System;
using Triangle.Audio;
using Triangle.Core;
using Triangle.Dream;
using Triangle.Nightmare;
using Triangle.Player;
using Triangle.Save;
using Triangle.UI;
using Triangle.World;
using UnityEngine.Rendering.Universal;
using UnityEngine;

namespace Triangle.Core
{
    /// <summary>
    /// Brings the dream online.
    ///
    /// The whole slice is code-driven, exactly as the Unreal vertical slice was: this
    /// component is the only thing a scene needs to contain. It creates the player,
    /// registers every service in <see cref="TriangleRuntime"/>, builds the childhood
    /// bedroom through the environment flow, and starts the Dream Director.
    ///
    /// Port of ATriangleGameMode::StartPlay plus the subsystem initialisation that
    /// Unreal performed through WorldSubsystems.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public class TriangleBootstrap : MonoBehaviour
    {

        [Header("Start")]
        [Tooltip("Which environment the dream opens in. YardOutside is the exterior with terrain.")]
        [SerializeField] private TriangleDreamEnvironment startEnvironment = TriangleDreamEnvironment.ChildhoodBedroom;

        [Header("Optional content")]
        [Tooltip("Dream bed. Leave empty and the dream runs on the procedural hum alone.")]
        [SerializeField] private AudioClip masterMusic;

        [Tooltip("Hide the cursor and lock it to the window on start.")]
        [SerializeField] private bool lockCursor = true;

        /// <summary>
        /// The environment the dream actually opens in: the serialized field, unless the
        /// command line overrides it.
        /// </summary>
        public TriangleDreamEnvironment StartEnvironment =>
            CommandLineEnvironmentOverride() ?? startEnvironment;

        /// <summary>
        /// Reads <c>-triangleEnv &lt;id&gt;</c> off the command line, if present and valid.
        ///
        /// A development affordance, and the only one that reaches a built player: the
        /// editor has the Jump To menu, but a standalone build has no menu bar and its
        /// scenes are baked, so an environment added after the last build could not
        /// otherwise be opened at all. The build already has to be relaunched to test a
        /// save change, so the cost of a fresh start is not new.
        ///
        /// A trailing integer is accepted too, matching the enum's declaration order, so
        /// the argument stays usable if a name is ever renamed - a bad name falls back
        /// to the serialized field rather than failing, because refusing to start a game
        /// over a typo is the worse failure.
        /// </summary>
        private TriangleDreamEnvironment? CommandLineEnvironmentOverride()
        {
            string[] args;
            try
            {
                args = Environment.GetCommandLineArgs();
            }
            catch (Exception e)
            {
                // Sandboxed or restricted hosts can refuse this. Not worth failing a boot.
                TriangleLog.Verbose("Command line unavailable: " + e.Message);
                return null;
            }

            var all = (TriangleDreamEnvironment[])System.Enum.GetValues(typeof(TriangleDreamEnvironment));

            string value = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-triangleEnv", StringComparison.OrdinalIgnoreCase))
                {
                    value = args[i + 1];
                    break;
                }
            }

            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            foreach (TriangleDreamEnvironment candidate in all)
            {
                if (string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(TriangleGameFlow.IdFor(candidate), value, StringComparison.OrdinalIgnoreCase))
                {
                    TriangleLog.Log($"Command line: starting in {candidate}.");
                    return candidate;
                }
            }

            if (int.TryParse(value, out int index) && index >= 0 && index < all.Length)
            {
                var byIndex = (TriangleDreamEnvironment)index;
                TriangleLog.Log($"Command line: starting in {byIndex} (index {index}).");
                return byIndex;
            }

            TriangleLog.Error($"-triangleEnv '{value}' is not an environment. Using the serialized start.");
            return null;
        }

        private void OnApplicationQuit()
        {
            // Stop the hum and the bed explicitly. On quit, Unity tears the audio
            // sources down as part of shutdown, and a streamed clip that is still
            // pulling from its PCM callback at that moment can emit a click or, on some
            // drivers, a hard fault rather than fading out.
            TriangleRuntime.Audio?.Shutdown();
        }

        private void OnApplicationPause(bool paused)
        {
            // Losing focus mid-audio-callback is the other common source of the same
            // fault, and the pause menu is not necessarily what brought us here.
            if (paused)
            {
                TriangleRuntime.Audio?.SetMusicState(TriangleAudioDirector.StateCalm, 0.1f);
            }
        }

        /// <summary>
        /// Captures the frame to a PNG and quits, when asked to on the command line.
        ///
        /// This exists because the whole project is procedural and every visual decision is
        /// invisible in a log. "The texture contrast was raised to 34%" is a claim; a
        /// screenshot of the spawn view is evidence, and it is the only evidence available
        /// for a headless build.
        ///
        /// <c>-triangleShot &lt;path&gt; [-triangleShotFrames &lt;n&gt;] [-triangleShotEnv &lt;id&gt;]</c>
        ///
        /// The frame count exists because the dream assembles in one frame and the volume
        /// stack needs a frame to apply - a capture on frame one shows the world before
        /// tonemapping. Waiting is cheaper than guessing the number.
        /// </summary>
        private void StartScreenshotCapture()
        {
            string path = ReadArgument("-triangleShot");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            int frames = Mathf.Max(1, ReadIntArgument("-triangleShotFrames", 8));
            string environment = ReadArgument("-triangleShotEnv");

            StartCoroutine(CaptureAfterFrames(path, frames, environment));
        }

        private System.Collections.IEnumerator CaptureAfterFrames(string path, int frames, string environment)
        {
            // An optional environment override, so one build can photograph every room.
            if (!string.IsNullOrEmpty(environment) && TriangleRuntime.GameFlow != null &&
                TriangleGameFlow.IsKnownEnvironmentId(environment))
            {
                TriangleRuntime.GameFlow.EnsureEnvironment(TriangleGameFlow.EnvironmentForId(environment));

                // The swap is immediate but the builder's Awake runs synchronously, so one
                // frame is enough for the world to exist and another for the probes.
                yield return null;
                yield return null;
            }

            // Wait out the requested frames. Unaffected by timescale, because the capture has
            // to happen even if something has paused the game.
            for (int i = 0; i < frames; i++)
            {
                yield return new WaitForEndOfFrame();
            }

            ScreenCapture.CaptureScreenshot(path);
            TriangleLog.Log($"Screenshot written to {path}.");

            // A frame for the capture to land on disk before the process exits.
            yield return null;

            if (Application.isBatchMode)
            {
                Application.Quit();
            }
        }

        private static string ReadArgument(string name)
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        return args[i + 1];
                    }
                }
            }
            catch (Exception e)
            {
                TriangleLog.Verbose("Command line unavailable: " + e.Message);
            }

            return null;
        }

        private static int ReadIntArgument(string name, int fallback)
        {
            string raw = ReadArgument(name);
            return int.TryParse(raw, out int parsed) ? parsed : fallback;
        }

        [Header("Frame pacing")]
        [Tooltip("Frames per second ceiling. Ignored while V Sync is on, which takes priority.")]
        [SerializeField] private int targetFrameRate = 60;

        private void Awake()
        {
            ApplyFramePacing();

            RegisterServices();
            CreatePostProcessing();
            CreatePlayer();
            CreateHud();
            CreateDirector();

            if (lockCursor)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        /// <summary>
        /// Reconciles the framerate ceiling against V Sync.
        ///
        /// The previous version hard-set both, which fight each other: with
        /// <c>vSyncCount &gt; 0</c> the GPU presents on the display's own refresh, and
        /// <c>Application.targetFrameRate</c> is ignored on desktop. Setting both meant
        /// the ceiling only ever took effect on a machine with V Sync disabled, and on a
        /// 144 Hz display the game was pinned to 60 with no way to ask otherwise.
        ///
        /// V Sync wins when it is enabled, because tearing on a slow-moving dream is far
        /// more objectionable than an occasional dropped frame. The ceiling applies
        /// only when V Sync is off, which is the case where it is the only limiter.
        /// </summary>
        private void ApplyFramePacing()
        {
            bool vSyncEnabled = QualitySettings.vSyncCount > 0;
            Application.targetFrameRate = vSyncEnabled ? -1 : Mathf.Max(15, targetFrameRate);

            TriangleLog.Log(vSyncEnabled
                ? $"Frame pacing: V Sync on at {QualitySettings.vSyncCount}; the framerate ceiling is not applied."
                : $"Frame pacing: V Sync off, framerate ceiling {Application.targetFrameRate}.");
        }

        private void Start()
        {
            ApplyVolumeSettings();
            BindAudioToDream();

            // The flow subsystem owns the active builder and moves the already-created
            // pawn onto the generated start.
            TriangleGameFlow flow = TriangleRuntime.GameFlow;
            if (flow == null)
            {
                TriangleLog.Error("GameFlow was not registered; the dream cannot be assembled.");
                return;
            }

            TriangleDreamEnvironment start = StartEnvironment;
            if (!flow.EnsureEnvironment(start))
            {
                TriangleLog.Error($"GameFlow could not assemble the {TriangleGameFlow.IdFor(start)}.");
            }

            // The pawn built its camera in Awake, so it exists by now and can be given its
            // render pipeline settings. Doing this here rather than in the pawn keeps the
            // pipeline knowledge in one place.
            ConfigureCamera(TriangleRuntime.Player?.Camera);

            TriangleRuntime.Director?.InitializeDream();
            TriangleRuntime.Hud?.BindToPlayer(TriangleRuntime.Player);

            if (masterMusic != null)
            {
                TriangleRuntime.Audio?.SetMasterMusic(masterMusic);
            }
            else
            {
                // No authored bed exists yet, so synthesise one rather than leaving
                // the state machine inaudible.
                TriangleRuntime.Audio?.EnsureFallbackBed();
            }

            TriangleLog.Log("The dream begins.");

            StartScreenshotCapture();

            // Everything above is procedural content generation and world assembly, and
            // all of it lands on this single frame. The per-kind figures are logged as
            // they happen; this is the total, which is what actually matters for how the
            // opening beat feels.
            if (UnityEngine.Debug.isDebugBuild)
            {
                TriangleLog.Log(
                    $"Dream assembled in {Time.realtimeSinceStartup:0.000}s since load; " +
                    $"{TriangleProceduralTextures.TotalGeneratedMilliseconds:0.0} ms of that was surface textures.");
            }
        }

        /// <summary>
        /// Pushes the player's volume preferences into the audio director.
        ///
        /// Music and hum are separate sliders rather than one master, because the hum is
        /// the thing most likely to be uncomfortable - it is a low sustained tone by
        /// design. A single volume control would force a choice between the bed and the
        /// presence cue, when the reason to want them apart is exactly that they are
        /// different things.
        /// </summary>
        private void ApplyVolumeSettings()
        {
            TriangleAudioDirector audio = TriangleRuntime.Audio;
            if (audio == null)
            {
                return;
            }

            audio.MusicBaseVolume = TriangleAccessibility.MusicVolume;
            audio.HumVolume = TriangleAccessibility.HumVolume;
        }

        /// <summary>
        /// Wires the audio bed to what the dream is actually doing.
        ///
        /// The bed was fully implemented - four named states, a fade, a silence override -
        /// but nothing ever changed its state, so it sat at Calm for the entire game. The
        /// director already raises exactly the signals the state machine wants, so the
        /// missing piece was the subscription rather than any new audio logic.
        ///
        /// The design intent this restores: the bed ducks as The Triangle arrives, drops
        /// to nothing at maximum presence, and recovers when the presence withdraws. The
        /// hum and the bed therefore move together rather than the hum rising over an
        /// unchanged bed.
        /// </summary>
        private void BindAudioToDream()
        {
            TriangleDreamDirector director = TriangleRuntime.Director;
            TriangleAudioDirector audio = TriangleRuntime.Audio;
            if (director == null || audio == null)
            {
                return;
            }

            const float bedFadeSeconds = 1.4f;

            director.OnPresenceStart += presence => audio.SetMusicState(
                presence >= 0.6f ? TriangleAudioDirector.StateManifest : TriangleAudioDirector.StateWatch,
                bedFadeSeconds);

            director.OnPresenceIncrease += presence =>
            {
                // Past the threshold the bed is all but gone, so the hum is alone.
                if (presence >= 0.6f)
                {
                    audio.SetMusicState(TriangleAudioDirector.StateManifest, bedFadeSeconds);
                }
            };

            director.OnPresenceDisappear += () =>
                audio.SetMusicState(TriangleAudioDirector.StateCalm, bedFadeSeconds * 1.6f);

            // A completed false awakening is the closest the slice gets to silence.
            director.OnFalseAwakening += _ =>
            {
                audio.SetNearSilence(true, 0.6f);
                audio.PushSilenceRelease(2.2f, () => audio.SetNearSilence(false, 2.5f));
            };

            // Volume changes mid-session have to reach the audio director, which is a
            // plain class rather than a component, so nothing would otherwise push them.
            TriangleAccessibility.OnSettingsChanged += ApplyVolumeSettings;

            TriangleLog.Verbose("Dream bed bound to the director's presence and wake signals.");
        }

        private void RegisterServices()
        {
            TriangleRuntime.Reset();

            // The scheduler must exist before anything that defers work through it.
            TriangleRuntime.Scheduler = GetComponent<TriangleScheduler>();
            if (TriangleRuntime.Scheduler == null)
            {
                TriangleRuntime.Scheduler = gameObject.AddComponent<TriangleScheduler>();
            }

            TriangleRuntime.Anomalies = new TriangleAnomalySystem { RootParent = transform };
            TriangleRuntime.Motifs = new TriangleMotifSystem();
            TriangleRuntime.GameFlow = new TriangleGameFlow(this);
            TriangleRuntime.Wake = new TriangleFalseAwakeningManager(TriangleRuntime.Scheduler);
            TriangleRuntime.Save = new TriangleSaveSystem();
            TriangleRuntime.Audio = new TriangleAudioDirector(this);
        }

        private void CreatePlayer()
        {
            var playerObject = new GameObject("TrianglePlayer");
            playerObject.transform.SetParent(transform, false);
            TriangleRuntime.Player = playerObject.AddComponent<TrianglePlayer>();
        }

        private void CreateHud()
        {
            var hudObject = new GameObject("TriangleHUD");
            hudObject.transform.SetParent(transform, false);
            TriangleRuntime.Hud = hudObject.AddComponent<TriangleHUD>();
        }

        private void CreateDirector()
        {
            var directorObject = new GameObject("DreamDirector");
            directorObject.transform.SetParent(transform, false);
            TriangleRuntime.Director = directorObject.AddComponent<TriangleDreamDirector>();
        }

        /// <summary>
        /// Brings the render pipeline's post-processing online.
        ///
        /// Created here rather than as a scene object so the playable scene keeps containing
        /// only the bootstrap, which is the constraint the whole code-driven architecture
        /// rests on.
        ///
        /// Before this existed the frame was raw linear radiance written straight to the
        /// screen. On a palette of near-black blue-grey surfaces that reads as flat and
        /// chalky, and no amount of lighting or texture work fully compensates - the
        /// tonemapping curve and the bloom are not polish here, they are what makes the
        /// image legible at all.
        /// </summary>
        private void CreatePostProcessing()
        {
            var postObject = new GameObject("TrianglePostProcessing");
            postObject.transform.SetParent(transform, false);
            postObject.AddComponent<Triangle.Render.TrianglePostProcessing>();
        }

        /// <summary>
        /// Configures the pawn's camera for the render pipeline.
        ///
        /// URP stores anti-aliasing and the post-processing flag on
        /// <c>UniversalAdditionalCameraData</c> rather than on the Camera, and the pawn
        /// builds its camera at runtime with AddComponent, so nothing ever gives it one.
        /// Without it the camera falls back to defaults - and because
        /// <c>renderPostProcessing</c> defaults off on a runtime-created camera, the entire
        /// volume stack is silently discarded while the volume itself still reports that it
        /// is active.
        /// </summary>
        private static void ConfigureCamera(Camera camera)
        {
            if (camera == null)
            {
                return;
            }

            // Returns the existing data component or creates one, which is exactly what a
            // camera built in code needs.
            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
            if (data == null)
            {
                TriangleLog.Warn("The pawn's camera has no Universal camera data; " +
                                 "post-processing and anti-aliasing are unavailable.");
                return;
            }

            // SMAA rather than FXAA. FXAA blurs across depth edges, which softens exactly the
            // hard box corners this scene is made of; SMAA is morphological and keeps them.
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality = AntialiasingQuality.High;

            // Read the post stack from the global volume rather than from a camera profile.
            data.renderPostProcessing = true;

            // The opaque and depth textures are read by post effects and by screen-space
            // occlusion. Requested per camera so the camera is self-describing rather than
            // depending on a global default that could be changed elsewhere.
            data.requiresColorOption = CameraOverrideOption.On;
            data.requiresDepthOption = CameraOverrideOption.On;

            // No camera stacking. There is one camera, and stacking exists to serve an
            // overlay camera this game does not have; cameraStack is read-only anyway,
            // since URP builds it from the cameras' own render type at runtime.
        }

        private void OnDestroy()
        {
            TriangleAccessibility.OnSettingsChanged -= ApplyVolumeSettings;

            TriangleRuntime.GameFlow?.Shutdown();
            TriangleRuntime.Audio?.Shutdown();

            // Reclaim the intrusion root before the service locator is cleared, so the
            // reference to it is still reachable.
            TriangleRuntime.Anomalies?.Shutdown();

            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            TriangleRuntime.Reset();
        }
    }
}
