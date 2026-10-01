using System;
using System.Collections.Generic;
using Triangle.Core;
using Triangle.Lucidity;
using Triangle.Player;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Triangle.UI
{
    /// <summary>
    /// The in-game HUD, built entirely in C# (no content assets required).
    /// Shows interaction prompts, optional Lucidity/Recognition meters, subtitles,
    /// and a full-screen transition veil for dream/environment changes.
    ///
    /// The HUD binds to the owning pawn's stat components rather than polling them.
    /// This keeps the bars correct after save/load and avoids retaining a destroyed
    /// player through a delegate.
    ///
    /// Port of ATriangleHUD + UTriangleHUDWidget. Unreal's C++ UMG tree becomes a
    /// programmatically assembled uGUI hierarchy.
    /// </summary>
    [DisallowMultipleComponent]
    public class TriangleHUD : MonoBehaviour
    {
        private struct QueuedSubtitle
        {
            public string Text;
            public float Duration;
        }

        private enum DreamTransitionPhase
        {
            None,
            FadingOut,
            Holding,
            FadingIn
        }

        [Header("Accessibility")]
        [Range(0.5f, 3f)] [SerializeField] private float subtitleScale = 1f;
        [Range(0f, 1f)] [SerializeField] private float subtitleOpacity = 1f;
        [SerializeField] private Color subtitleColor = new Color(0.92f, 0.92f, 0.96f, 1f);

        [Header("Interaction prompt")]
        [Tooltip("Shown beside the focused object's name, e.g. \"[F / X]\". Update this if the Interact binding changes.")]
        [SerializeField] private string interactionHint = "[F / X]";

        [Header("Subtitles")]
        [Tooltip("Prevents an unbounded burst of events from growing memory forever.")]
        [Range(1, 32)] [SerializeField] private int maxSubtitleQueue = 16;

        [Tooltip("Dark plate behind the subtitle, so reduced-opacity text stays legible against the dark bed.")]
        [SerializeField] private bool subtitleBackdrop = false;

        private Canvas _canvas;
        private Text _interactionName;
        private Text _interactionAction;
        private Image _lucidityBar;
        private Text _lucidityLabel;
        private Image _recognitionBar;
        private Text _recognitionLabel;
        private Text _subtitleText;
        private Image _subtitleBackdrop;
        private Text _pauseText;
        private Image _dreamFadeOverlay;

        private readonly Queue<QueuedSubtitle> _subtitleQueue = new Queue<QueuedSubtitle>();
        private float _subtitleRemaining;
        private bool _subtitleActive;

        private Color _dreamFadeColor = Color.black;
        private float _dreamFadeAlpha;
        private float _dreamFadeOutDuration;
        private float _dreamFadeHoldDuration;
        private float _dreamFadeInDuration;
        private float _dreamTransitionElapsed;
        private DreamTransitionPhase _dreamTransitionPhase = DreamTransitionPhase.None;

        private TrianglePlayer _boundPlayer;

        public bool IsPaused { get; private set; }

        /// <summary>Fired at full opacity, when the screen is fully covered.</summary>
        public event System.Action OnDreamTransitionMidpoint;

        /// <summary>Fired when the veil has finished fading back in.</summary>
        public event System.Action OnDreamTransitionFinished;

        private void Awake()
        {
            BuildHud();

            // Read the stored settings before anything is shown, so the very first
            // subtitle already honours the player's subtitle size and opacity.
            ApplyAccessibilitySettings();
            TriangleAccessibility.OnSettingsChanged += ApplyAccessibilitySettings;
        }

        private void OnDestroy()
        {
            UnbindFromPlayer();

            // Unhook from the static event. A HUD that outlives the domain - which a
            // disabled-then-destroyed object can - would otherwise keep the whole
            // settings object alive through this delegate.
            TriangleAccessibility.OnSettingsChanged -= ApplyAccessibilitySettings;
        }

        // --- Construction -----------------------------------------------------------

        private static Font LoadDefaultFont()
        {
            // Unity 2022 replaced the built-in Arial with LegacyRuntime.
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
            {
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }

            return font;
        }

        private void BuildHud()
        {
            if (_canvas != null)
            {
                return;
            }

            Font font = LoadDefaultFont();

            var canvasObject = new GameObject("TriangleCanvas");
            canvasObject.transform.SetParent(transform, false);
            _canvas = canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            if (FindObjectOfType<EventSystem>() == null)
            {
                // The HUD is built entirely from Text and Image, neither of which handles
                // pointer input, so no input module is needed. Adding a
                // StandaloneInputModule would have it poll the mouse and every axis in the
                // Input Manager every frame for no possible consumer, and would swallow
                // nothing useful - the player reads input directly through Input.*.
                var eventSystemObject = new GameObject("EventSystem");
                eventSystemObject.transform.SetParent(transform, false);
                eventSystemObject.AddComponent<EventSystem>();
            }

            // Full-screen transition veil. Sits first so everything else draws on top.
            _dreamFadeOverlay = CreateImage("DreamFadeOverlay", canvasObject.transform, Color.black);
            StretchFull(_dreamFadeOverlay.rectTransform);
            _dreamFadeOverlay.raycastTarget = false;
            ApplyDreamFade(0f);

            // Interaction prompt, bottom centre.
            _interactionName = CreateText("InteractionName", canvasObject.transform, font, 26, TextAnchor.MiddleCenter);
            _interactionName.color = new Color(0.9f, 0.9f, 0.92f);
            _interactionName.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            _interactionName.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            _interactionName.rectTransform.pivot = new Vector2(0.5f, 0f);
            _interactionName.rectTransform.anchoredPosition = new Vector2(0f, 132f);
            _interactionName.rectTransform.sizeDelta = new Vector2(760f, 40f);

            _interactionAction = CreateText("InteractionAction", canvasObject.transform, font, 20, TextAnchor.MiddleCenter);
            _interactionAction.color = new Color(0.75f, 0.78f, 0.82f);
            _interactionAction.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            _interactionAction.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            _interactionAction.rectTransform.pivot = new Vector2(0.5f, 0f);
            _interactionAction.rectTransform.anchoredPosition = new Vector2(0f, 100f);
            _interactionAction.rectTransform.sizeDelta = new Vector2(760f, 30f);

            // Lucidity meter, bottom left.
            _lucidityLabel = CreateText("LucidityLabel", canvasObject.transform, font, 18, TextAnchor.MiddleLeft);
            _lucidityLabel.color = new Color(0.82f, 0.86f, 0.92f);
            _lucidityLabel.rectTransform.anchorMin = new Vector2(0f, 0f);
            _lucidityLabel.rectTransform.anchorMax = new Vector2(0f, 0f);
            _lucidityLabel.rectTransform.pivot = new Vector2(0f, 0f);
            _lucidityLabel.rectTransform.anchoredPosition = new Vector2(40f, 46f);
            _lucidityLabel.rectTransform.sizeDelta = new Vector2(320f, 26f);
            _lucidityLabel.text = "LUCIDITY";

            _lucidityBar = CreateBar("LucidityBar", canvasObject.transform, new Color(0.55f, 0.78f, 0.92f, 0.85f));
            _lucidityBar.rectTransform.anchorMin = new Vector2(0f, 0f);
            _lucidityBar.rectTransform.anchorMax = new Vector2(0f, 0f);
            _lucidityBar.rectTransform.pivot = new Vector2(0f, 0f);
            _lucidityBar.rectTransform.anchoredPosition = new Vector2(40f, 34f);
            _lucidityBar.rectTransform.sizeDelta = new Vector2(320f, 8f);

            // Recognition meter, bottom right.
            _recognitionLabel = CreateText("RecognitionLabel", canvasObject.transform, font, 18, TextAnchor.MiddleRight);
            _recognitionLabel.color = new Color(0.86f, 0.80f, 0.86f);
            _recognitionLabel.rectTransform.anchorMin = new Vector2(1f, 0f);
            _recognitionLabel.rectTransform.anchorMax = new Vector2(1f, 0f);
            _recognitionLabel.rectTransform.pivot = new Vector2(1f, 0f);
            _recognitionLabel.rectTransform.anchoredPosition = new Vector2(-40f, 46f);
            _recognitionLabel.rectTransform.sizeDelta = new Vector2(320f, 26f);
            _recognitionLabel.text = "RECOGNITION";

            _recognitionBar = CreateBar("RecognitionBar", canvasObject.transform, new Color(0.82f, 0.60f, 0.86f, 0.85f));
            _recognitionBar.rectTransform.anchorMin = new Vector2(1f, 0f);
            _recognitionBar.rectTransform.anchorMax = new Vector2(1f, 0f);
            _recognitionBar.rectTransform.pivot = new Vector2(1f, 0f);
            _recognitionBar.rectTransform.anchoredPosition = new Vector2(-40f, 34f);
            _recognitionBar.rectTransform.sizeDelta = new Vector2(320f, 8f);

            // Subtitle, lower third. The backdrop is a sibling rather than a parent, so it
            // cannot scale or clip with the text and the two can be shown independently.
            _subtitleBackdrop = CreateImage("SubtitleBackdrop", canvasObject.transform, new Color(0f, 0f, 0f, 0.55f));
            _subtitleBackdrop.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            _subtitleBackdrop.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            _subtitleBackdrop.rectTransform.pivot = new Vector2(0.5f, 0f);
            _subtitleBackdrop.rectTransform.anchoredPosition = new Vector2(0f, 200f);
            _subtitleBackdrop.rectTransform.sizeDelta = new Vector2(1560f, 120f);
            _subtitleBackdrop.raycastTarget = false;

            _subtitleText = CreateText("Subtitle", canvasObject.transform, font, 30, TextAnchor.LowerCenter);
            _subtitleText.color = subtitleColor;
            _subtitleText.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            _subtitleText.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            _subtitleText.rectTransform.pivot = new Vector2(0.5f, 0f);
            _subtitleText.rectTransform.anchoredPosition = new Vector2(0f, 200f);
            _subtitleText.rectTransform.sizeDelta = new Vector2(1500f, 140f);
            _subtitleText.raycastTarget = false;
            ApplySubtitleAppearance();
            SetSubtitleText(string.Empty);

            // Pause, dead centre.
            _pauseText = CreateText("Pause", canvasObject.transform, font, 44, TextAnchor.MiddleCenter);
            _pauseText.color = new Color(0.9f, 0.9f, 0.92f);
            StretchFull(_pauseText.rectTransform);
            _pauseText.text = "PAUSED";
            _pauseText.gameObject.SetActive(false);

            // Both awareness meters are hidden by default. They are revealed by the
            // band-crossing handlers, and BindToPlayer re-derives their visibility for
            // a pawn that starts or loads above the reveal threshold.
            SetLucidityMeterVisible(false);
            SetRecognitionMeterVisible(false);

            // Added disabled rather than not added at all. The HUD currently has no
            // interactive element, so the raycaster has nothing to test against, but
            // authoring a pause menu later would otherwise need this plumbing rebuilt -
            // and forgetting to re-enable it is a silent, hard-to-diagnose failure.
            canvasObject.AddComponent<GraphicRaycaster>().enabled = false;
        }

        private static Image CreateImage(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Image image = go.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private static Text CreateText(string name, Transform parent, Font font, int size, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Text text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        private static Image CreateBar(string name, Transform parent, Color color)
        {
            var background = CreateImage(name + "Background", parent, new Color(0f, 0f, 0f, 0.45f));
            StretchFull(background.rectTransform);

            Image bar = CreateImage(name, background.transform, color);
            bar.type = Image.Type.Filled;
            bar.fillMethod = Image.FillMethod.Horizontal;
            bar.fillAmount = 0f;
            bar.raycastTarget = false;
            StretchFull(bar.rectTransform);
            return bar;
        }

        private static void StretchFull(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        // --- Player binding -----------------------------------------------------------

        public void BindToPlayer(TrianglePlayer player)
        {
            UnbindFromPlayer();

            if (player == null)
            {
                return;
            }

            _boundPlayer = player;
            player.Lucidity.OnLucidityChanged += HandleLucidityChanged;
            player.Lucidity.OnLucidityLevelChanged += HandleLucidityLevelChanged;
            player.Recognition.OnRecognitionChanged += HandleRecognitionChanged;
            player.Recognition.OnRecognitionLevelChanged += HandleRecognitionLevelChanged;

            RefreshLucidityDisplay();
            RefreshRecognitionDisplay();

            // Derive visibility from the level the pawn is already at rather than
            // waiting for the next band crossing. Both restore paths assign the value
            // directly and deliberately do not fire a level event, so a load into High
            // Lucidity or Master Recognition would otherwise leave both meters hidden
            // for the rest of the run.
            SetLucidityMeterVisible(player.Lucidity.GetLucidityLevel() >= TriangleLucidityLevel.Medium);
            SetRecognitionMeterVisible(player.Recognition.GetRecognitionLevel() >= TriangleRecognitionLevel.Noticing);
        }

        public void UnbindFromPlayer()
        {
            if (_boundPlayer == null)
            {
                _boundPlayer = null;
                return;
            }

            _boundPlayer.Lucidity.OnLucidityChanged -= HandleLucidityChanged;
            _boundPlayer.Lucidity.OnLucidityLevelChanged -= HandleLucidityLevelChanged;
            _boundPlayer.Recognition.OnRecognitionChanged -= HandleRecognitionChanged;
            _boundPlayer.Recognition.OnRecognitionLevelChanged -= HandleRecognitionLevelChanged;
            _boundPlayer = null;
        }

        public bool IsBoundToPlayer() => _boundPlayer != null;

        // --- Meters ---------------------------------------------------------------------

        private void HandleLucidityChanged(float value) => RefreshLucidityDisplay();

        private void HandleLucidityLevelChanged(TriangleLucidityLevel level)
        {
            // The meter only appears once the player is noticing their own awareness.
            // The previous test was "level >= Low", and Low is the zero value of the
            // enum, so it was true for every level and the supposedly hidden meter was
            // on screen from the first frame. Medium is the first band the design says
            // the dream stops being accepted at.
            SetLucidityMeterVisible(level >= TriangleLucidityLevel.Medium);
        }

        private void HandleRecognitionChanged(float value) => RefreshRecognitionDisplay();

        private void HandleRecognitionLevelChanged(TriangleRecognitionLevel level)
        {
            // Recognition stays hidden until the player has started to notice a pattern.
            SetRecognitionMeterVisible(level >= TriangleRecognitionLevel.Noticing);
        }

        private void RefreshLucidityDisplay()
        {
            if (_lucidityBar == null || _boundPlayer == null)
            {
                return;
            }

            _lucidityBar.fillAmount = _boundPlayer.Lucidity.GetLucidity() / TriangleLucidity.Max;
        }

        private void RefreshRecognitionDisplay()
        {
            if (_recognitionBar == null || _boundPlayer == null)
            {
                return;
            }

            _recognitionBar.fillAmount = _boundPlayer.Recognition.GetRecognition() / TriangleRecognition.Max;
        }

        public void SetLucidityValue(float value01)
        {
            if (_lucidityBar != null)
            {
                _lucidityBar.fillAmount = Mathf.Clamp01(value01);
            }
        }

        public void SetLucidityMeterVisible(bool visible)
        {
            if (_lucidityBar != null)
            {
                _lucidityBar.transform.parent.gameObject.SetActive(visible);
            }

            if (_lucidityLabel != null)
            {
                _lucidityLabel.gameObject.SetActive(visible);
            }
        }

        public void SetRecognitionValue(float value01)
        {
            if (_recognitionBar != null)
            {
                _recognitionBar.fillAmount = Mathf.Clamp01(value01);
            }
        }

        public void SetRecognitionMeterVisible(bool visible)
        {
            if (_recognitionBar != null)
            {
                _recognitionBar.transform.parent.gameObject.SetActive(visible);
            }

            if (_recognitionLabel != null)
            {
                _recognitionLabel.gameObject.SetActive(visible);
            }
        }

        // --- Interaction prompt -----------------------------------------------------------

        public void SetInteractionPrompt(string text, string actionName)
        {
            if (_interactionName != null)
            {
                _interactionName.text = text ?? string.Empty;
            }

            if (_interactionAction != null)
            {
                _interactionAction.text = actionName ?? string.Empty;
            }
        }

        /// <summary>
        /// The "what to press" half of the interaction prompt.
        ///
        /// An authored value rather than something derived. The legacy Input Manager
        /// exposes no public API for reading a binding's key or button name at runtime -
        /// there is no Input.GetKeyName - and the InputManager asset is not reachable
        /// from a player build, so the only correct-by-construction option would be to
        /// move to the Input System package and carry a serialized action asset.
        ///
        /// Keeping it as a field is still a real improvement over the previous state,
        /// where the literal "[F / X]" sat in the middle of the interaction scan: it is
        /// now tunable, sits with the rest of the HUD's presentation, and is changed in
        /// one place when a binding changes rather than by finding a string in a
        /// gameplay method.
        /// </summary>
        public string InteractionHint => interactionHint;

        // --- Subtitles ------------------------------------------------------------------------

        /// <summary>Queues a subtitle so closely-spaced story beats are not overwritten.</summary>
        public void PushSubtitle(string text, float duration)
        {
            if (string.IsNullOrEmpty(text) || _subtitleText == null)
            {
                return;
            }

            if (_subtitleQueue.Count >= maxSubtitleQueue)
            {
                // Drop the oldest queued beat rather than growing without bound.
                _subtitleQueue.Dequeue();
            }

            _subtitleQueue.Enqueue(new QueuedSubtitle { Text = text, Duration = Mathf.Max(0.1f, duration) });

            if (!_subtitleActive)
            {
                ShowNextSubtitle();
            }
        }

        private void ShowNextSubtitle()
        {
            if (_subtitleQueue.Count == 0)
            {
                _subtitleActive = false;
                SetSubtitleText(string.Empty);
                return;
            }

            QueuedSubtitle next = _subtitleQueue.Dequeue();
            _subtitleRemaining = next.Duration;
            _subtitleActive = true;
            SetSubtitleText(next.Text);
        }

        private void SetSubtitleText(string text)
        {
            if (_subtitleText == null)
            {
                return;
            }

            bool hasText = !string.IsNullOrEmpty(text);
            _subtitleText.text = text ?? string.Empty;
            _subtitleText.gameObject.SetActive(hasText);

            // The plate follows the text's visibility. Left on its own it would sit as a
            // dark rectangle in the middle of an empty screen.
            if (_subtitleBackdrop != null)
            {
                _subtitleBackdrop.gameObject.SetActive(hasText && subtitleBackdrop);
            }
        }

        public void SetSubtitleScale(float scale)
        {
            subtitleScale = Mathf.Clamp(scale, 0.5f, 3f);
            ApplySubtitleAppearance();
        }

        public void SetSubtitleOpacity(float opacity)
        {
            subtitleOpacity = Mathf.Clamp01(opacity);
            ApplySubtitleAppearance();
        }

        private void ApplySubtitleAppearance()
        {
            if (_subtitleText == null)
            {
                return;
            }

            _subtitleText.fontSize = Mathf.RoundToInt(30f * subtitleScale);
            Color color = subtitleColor;
            color.a = subtitleOpacity;
            _subtitleText.color = color;

            // The plate tracks the text's alpha so reducing opacity dims both together
            // rather than leaving a fully opaque box behind nearly invisible text.
            if (_subtitleBackdrop != null)
            {
                Color plate = _subtitleBackdrop.color;
                plate.a = 0.55f * subtitleOpacity;
                _subtitleBackdrop.color = plate;
                _subtitleBackdrop.gameObject.SetActive(subtitleBackdrop && _subtitleText.gameObject.activeSelf);
            }
        }

        /// <summary>
        /// Pulls the current accessibility settings into the HUD's presentation state.
        ///
        /// Subscribed to the settings change event, so an adjustment made in the pause
        /// menu is visible immediately rather than on the next dream.
        /// </summary>
        private void ApplyAccessibilitySettings()
        {
            subtitleScale = TriangleAccessibility.SubtitleScale;
            subtitleOpacity = TriangleAccessibility.SubtitleOpacity;
            subtitleBackdrop = TriangleAccessibility.SubtitleBackdrop;
            ApplySubtitleAppearance();
        }

        public void SetSubtitleBackdrop(bool enabled)
        {
            subtitleBackdrop = enabled;
            ApplySubtitleAppearance();
        }

        // --- Dream transition veil ------------------------------------------------------------

        /// <summary>
        /// Fades the screen to FadeColor, holds at full opacity, then fades back.
        /// The midpoint event is the integration point for swapping a dream
        /// environment while the screen is fully covered.
        /// </summary>
        public void StartDreamTransition(float fadeOutDuration, float holdDuration, float fadeInDuration, Color fadeColor)
        {
            _dreamFadeColor = fadeColor;
            _dreamFadeOutDuration = Mathf.Max(0.01f, fadeOutDuration);
            _dreamFadeHoldDuration = Mathf.Max(0f, holdDuration);
            _dreamFadeInDuration = Mathf.Max(0.01f, fadeInDuration);
            _dreamTransitionElapsed = 0f;
            _dreamTransitionPhase = DreamTransitionPhase.FadingOut;
        }

        /// <summary>Allows a caller to drive the veil manually.</summary>
        public void SetDreamFadeAlpha(float alpha)
        {
            ApplyDreamFade(Mathf.Clamp01(alpha));
        }

        public void StopDreamTransition()
        {
            _dreamTransitionPhase = DreamTransitionPhase.None;
            _dreamTransitionElapsed = 0f;
            ApplyDreamFade(0f);
        }

        public bool IsDreamTransitionActive() => _dreamTransitionPhase != DreamTransitionPhase.None;

        private void ApplyDreamFade(float alpha)
        {
            _dreamFadeAlpha = Mathf.Clamp01(alpha);
            if (_dreamFadeOverlay != null)
            {
                Color color = _dreamFadeColor;
                color.a = _dreamFadeAlpha;
                _dreamFadeOverlay.color = color;
                _dreamFadeOverlay.gameObject.SetActive(_dreamFadeAlpha > 0.001f);
            }
        }

        // --- Pause --------------------------------------------------------------------------------

        /// <summary>
        /// The HUD is the single owner of the pause state.
        ///
        /// Pause used to be a boolean on this class while the pawn separately assigned
        /// <c>Time.timeScale</c> and the cursor, with the player reading the boolean
        /// back to decide what to toggle. Any other system touching the timescale
        /// desynchronised the two and the next Escape did the opposite of what was
        /// asked. One method owns all three now.
        /// </summary>
        public void TogglePause()
        {
            SetPaused(!IsPaused);
        }

        public void SetPaused(bool paused)
        {
            IsPaused = paused;
            Time.timeScale = paused ? 0f : 1f;
            Cursor.lockState = paused ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = paused;

            if (_pauseText != null)
            {
                _pauseText.gameObject.SetActive(paused);
            }
        }

        private void OnDisable()
        {
            // Leaving play mode, or disabling the HUD, must not leave the game frozen.
            if (IsPaused)
            {
                Time.timeScale = 1f;
                IsPaused = false;
            }
        }

        // --- Tick ------------------------------------------------------------------------------------

        private void Update()
        {
            // Subtitles read as presentation, so they ride scaled time and hold still
            // while the game is paused. The transition veil must not: it is a
            // screen-covering effect with a hard midpoint, and driving it on scaled
            // time means any pause landing inside the window leaves the player looking
            // at an opaque black screen with no way to tell that is a pause rather than
            // a hung game. Presentation, not simulation, gets unscaled time.
            float scaledDelta = Time.deltaTime;
            float unscaledDelta = Time.unscaledDeltaTime;

            if (_subtitleActive)
            {
                _subtitleRemaining -= scaledDelta;
                if (_subtitleRemaining <= 0f)
                {
                    ShowNextSubtitle();
                }
            }

            AdvanceDreamTransition(unscaledDelta);
        }

        private void AdvanceDreamTransition(float deltaSeconds)
        {
            if (_dreamTransitionPhase == DreamTransitionPhase.None)
            {
                return;
            }

            _dreamTransitionElapsed += deltaSeconds;

            switch (_dreamTransitionPhase)
            {
                case DreamTransitionPhase.FadingOut:
                    if (_dreamTransitionElapsed >= _dreamFadeOutDuration)
                    {
                        ApplyDreamFade(1f);
                        _dreamTransitionElapsed -= _dreamFadeOutDuration;
                        _dreamTransitionPhase = _dreamFadeHoldDuration > 0f
                            ? DreamTransitionPhase.Holding
                            : DreamTransitionPhase.FadingIn;
                        OnDreamTransitionMidpoint?.Invoke();
                    }
                    else
                    {
                        ApplyDreamFade(_dreamTransitionElapsed / _dreamFadeOutDuration);
                    }

                    break;

                case DreamTransitionPhase.Holding:
                    if (_dreamTransitionElapsed >= _dreamFadeHoldDuration)
                    {
                        _dreamTransitionElapsed -= _dreamFadeHoldDuration;
                        _dreamTransitionPhase = DreamTransitionPhase.FadingIn;
                    }

                    break;

                case DreamTransitionPhase.FadingIn:
                    if (_dreamTransitionElapsed >= _dreamFadeInDuration)
                    {
                        StopDreamTransition();
                        OnDreamTransitionFinished?.Invoke();
                    }
                    else
                    {
                        ApplyDreamFade(1f - (_dreamTransitionElapsed / _dreamFadeInDuration));
                    }

                    break;
            }
        }
    }
}
