using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Makes a UI element (crosshair) follow the mouse cursor position.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class CrosshairFollowMouse : MonoBehaviour
{
    [SerializeField] private Vector2 offset = Vector2.zero;

    private RectTransform rectTransform;
    private Canvas canvas;
    private CanvasGroup canvasGroup;
    private KnockKnockArena.Gameplay.DynamicCrosshair dynamicCrosshair;

    void Start()
    {
        rectTransform = GetComponent<RectTransform>();
        canvas = GetComponentInParent<Canvas>();
        canvasGroup = GetComponent<CanvasGroup>();
        dynamicCrosshair = GetComponent<KnockKnockArena.Gameplay.DynamicCrosshair>();

        if (canvasGroup == null)
        {   
            canvasGroup = gameObject.AddComponent<CanvasGroup>();
        }

        // Make sure the crosshair doesn't block raycasts
        canvasGroup.blocksRaycasts = false;

        if (canvas == null)
        {
            Debug.LogError("CrosshairFollowMouse: No Canvas found in parent hierarchy!");
            enabled = false;
        }
    }

    void OnEnable()
    {
        if (canvasGroup != null)
            canvasGroup.alpha = 1f;
    }

    void OnDisable()
    {
        if (canvasGroup != null)
            canvasGroup.alpha = 0f;
    }

    void Update()
    {
        if (rectTransform == null || canvas == null) return;

        // No crosshair shown (login screen, disconnected): give the player the
        // ordinary mouse cursor back. Hiding it unconditionally left the login menu
        // with no visible pointer at all.
        if (dynamicCrosshair != null && !dynamicCrosshair.IsVisible)
        {
            canvasGroup.alpha = 0f;
            Cursor.visible = true;
            return;
        }

        // Hide crosshair when hovering over UI elements, show system cursor instead
        bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        canvasGroup.alpha = overUI ? 0f : 1f;
        Cursor.visible = overUI;

        // Convert mouse screen position to canvas position
        if (Mouse.current == null) return;
        Vector2 mousePosition = Mouse.current.position.ReadValue();

        if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
        {
            // For Screen Space - Overlay, mouse position is directly usable
            rectTransform.position = mousePosition + offset;
        }
        else if (canvas.renderMode == RenderMode.ScreenSpaceCamera)
        {
            // For Screen Space - Camera, convert screen to world point
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvas.transform as RectTransform,
                mousePosition,
                canvas.worldCamera,
                out Vector2 canvasPos
            );
            rectTransform.localPosition = canvasPos + offset;
        }
    }
}
