using Triangle.Audio;
using Triangle.Core;
using Triangle.Dream;
using Triangle.Nightmare;
using Triangle.Player;
using Triangle.Save;
using Triangle.UI;
using Triangle.World;
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
        [Tooltip("Which environment the dream opens in.")]
        [SerializeField] private TriangleDreamEnvironment startEnvironment = TriangleDreamEnvironment.ChildhoodBedroom;

        [Header("Optional content")]
        [Tooltip("Dream bed. Leave empty and the dream runs on the procedural hum alone.")]
        [SerializeField] private AudioClip masterMusic;

        [Tooltip("Hide the cursor and lock it to the window on start.")]
        [SerializeField] private bool lockCursor = true;

        private void Awake()
        {
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 1;

            RegisterServices();
            CreatePlayer();
            CreateHud();
            CreateDirector();

            if (lockCursor)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private void Start()
        {
            // The flow subsystem owns the active builder and moves the already-created
            // pawn onto the generated start.
            TriangleGameFlow flow = TriangleRuntime.GameFlow;
            if (flow == null)
            {
                TriangleLog.Error("GameFlow was not registered; the dream cannot be assembled.");
                return;
            }

            if (!flow.EnsureEnvironment(startEnvironment))
            {
                TriangleLog.Error($"GameFlow could not assemble the {TriangleGameFlow.IdFor(startEnvironment)}.");
            }

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

            TriangleRuntime.Anomalies = new TriangleAnomalySystem();
            TriangleRuntime.Motifs = new TriangleMotifSystem();
            TriangleRuntime.GameFlow = new TriangleGameFlow(this);
            TriangleRuntime.Wake = new TriangleFalseAwakeningManager(TriangleRuntime.Scheduler);
            TriangleRuntime.Save = new TriangleSaveSystem(this);
            TriangleRuntime.Audio = new TriangleAudioDirector(transform);
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

        private void OnDestroy()
        {
            TriangleRuntime.GameFlow?.Shutdown();
            TriangleRuntime.Audio?.Shutdown();
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            TriangleRuntime.Reset();
        }
    }
}
