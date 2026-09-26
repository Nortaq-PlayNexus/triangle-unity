using Triangle.Core;
using Triangle.Interaction;
using Triangle.Lucidity;
using Triangle.World;
using UnityEngine;

namespace Triangle.World
{
    /// <summary>
    /// The notebook. Fills as the player explores; each read can reveal the next
    /// page, and re-reading an already-discovered page grants nothing new.
    ///
    /// Port of ATriangleNotebook.
    /// </summary>
    [DisallowMultipleComponent]
    public class TriangleNotebook : MonoBehaviour
    {
        private const int PageCount = 3;

        private static readonly string[] Pages =
        {
            "\"Mostly blank. One page at the front has a triangle drawn in pencil.\"",
            "\"This is your handwriting. You don't remember writing it.\"",
            "\"The last note says: wake up.\""
        };

        [SerializeField] private float maxInteractionRange = Cm.M(200f);

        private TriangleInteractable _interactable;
        private int _interactionCount;

        public int InteractionCount => _interactionCount;

        private void Awake()
        {
            GameObject cover = TriangleShapes.CreateShape("Cover", TriangleShapes.Cube, transform);
            cover.transform.localScale = Cm.Scale(0.24f, 0.18f, 0.035f);
            MeshRenderer coverRenderer = cover.GetComponent<MeshRenderer>();
            coverRenderer.sharedMaterial = TriangleShapes.GetMaterial(new Color(0.42f, 0.38f, 0.30f));
            cover.AddComponent<BoxCollider>().size = Vector3.one;

            _interactable = gameObject.AddComponent<TriangleInteractable>();
            _interactable.InteractionName = "Notebook";
            _interactable.InteractionAnchor = cover.transform;
            _interactable.MaxInteractionRange = maxInteractionRange;
            _interactable.OnInteracted += HandleInteracted;
        }

        private void HandleInteracted(TriangleInteractable interactable, GameObject interactor)
        {
            _interactionCount = Mathf.Max(0, _interactionCount);
            if (_interactionCount < int.MaxValue)
            {
                _interactionCount++;
            }

            // Each read can reveal the next page, but reading an already-discovered
            // page must not grant the realization reward again.
            bool newPage = _interactionCount > 0 && _interactionCount <= PageCount;
            if (newPage)
            {
                TriangleLucidity lucidity = TriangleRuntime.Player != null ? TriangleRuntime.Player.Lucidity : null;
                lucidity?.AddLucidity(lucidity.BaseGainPerRealization);
            }

            int pageIndex = Mathf.Clamp(_interactionCount - 1, 0, PageCount - 1);
            ShowPage(pageIndex);
            TriangleLog.Dream($"Notebook read (page {pageIndex + 1}/{PageCount}, new={newPage}).");
        }

        public void SetInteractionCount(int newInteractionCount)
        {
            _interactionCount = Mathf.Max(0, newInteractionCount);
        }

        public void ResetInteractionCount()
        {
            _interactionCount = 0;
        }

        private void ShowPage(int pageIndex)
        {
            int index = Mathf.Clamp(pageIndex, 0, PageCount - 1);
            TriangleRuntime.Hud?.PushSubtitle(Pages[index], 4f);
        }
    }
}
