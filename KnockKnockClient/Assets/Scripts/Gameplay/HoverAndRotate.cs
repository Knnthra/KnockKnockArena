using UnityEngine;

public class HoverAndRotate : MonoBehaviour
{
    [SerializeField] private float hoverAmplitude = 0.2f;
    [SerializeField] private float hoverSpeed = 2f;
    [SerializeField] private float rotateSpeed = 90f;
    [SerializeField] private float hoverHeight = 0.3f;  // base height above landing position

    private Vector3 startPosition;
    private bool initialized;

    void OnEnable()
    {
        initialized = false;
    }

    void Update()
    {
        if (!initialized)
        {
            startPosition = transform.localPosition + Vector3.up * hoverHeight;
            initialized = true;
        }

        float hover = Mathf.Sin(Time.time * hoverSpeed) * hoverAmplitude;
        transform.localPosition = startPosition + Vector3.up * hover;
        transform.Rotate(Vector3.up, rotateSpeed * Time.deltaTime, Space.World);
    }
}
