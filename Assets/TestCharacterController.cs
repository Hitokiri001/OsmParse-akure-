using UnityEngine;

/// <summary>
/// Simple test character controller — attach to any GameObject.
/// WASD/Arrow keys to move, mouse to rotate camera, Q/E to go up/down.
/// No physics — moves the transform directly so it works regardless
/// of terrain collider state.
///
/// Assign the Camera as a child of this GameObject and it will follow.
/// Or assign CameraTransform manually to use a separate camera.
/// </summary>
public class TestCharacterController : MonoBehaviour
{
    [Header("Movement")]
    public float MoveSpeed    = 20f;
    public float SprintSpeed  = 60f;  // hold Left Shift to sprint
    public float VerticalSpeed = 10f; // Q = down, E = up

    [Header("Camera")]
    public Transform CameraTransform; // assign camera here, or leave null to use Camera.main
    public float MouseSensitivity = 2f;
    public float MinPitch = -80f;
    public float MaxPitch =  80f;

    [Header("Cursor")]
    public bool LockCursorOnStart = true;

    private float _yaw;    // horizontal rotation (Y axis) on this transform
    private float _pitch;  // vertical rotation (X axis) on camera only

    private void Start()
    {
        if (CameraTransform == null && Camera.main != null)
            CameraTransform = Camera.main.transform;

        // Initialise yaw from current rotation so we don't snap on start
        _yaw   = transform.eulerAngles.y;
        _pitch = CameraTransform != null
            ? CameraTransform.localEulerAngles.x
            : 0f;

        if (LockCursorOnStart)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;
        }
    }

    private void Update()
    {
        HandleCursor();
        HandleRotation();
        HandleMovement();
    }

    private void HandleCursor()
    {
        // Press Escape to release cursor, click to re-lock
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;
        }
        else if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;
        }
    }

    private void HandleRotation()
    {
        if (Cursor.lockState != CursorLockMode.Locked) return;

        float mouseX = Input.GetAxis("Mouse X") * MouseSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * MouseSensitivity;

        // Yaw rotates the whole body left/right
        _yaw  += mouseX;
        transform.rotation = Quaternion.Euler(0f, _yaw, 0f);

        // Pitch only rotates the camera up/down
        if (CameraTransform != null)
        {
            _pitch = Mathf.Clamp(_pitch - mouseY, MinPitch, MaxPitch);
            CameraTransform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }
    }

    private void HandleMovement()
    {
        float speed = Input.GetKey(KeyCode.LeftShift) ? SprintSpeed : MoveSpeed;

        // Forward/back and strafe relative to where we are facing
        float h = Input.GetAxisRaw("Horizontal"); // A/D or Left/Right
        float v = Input.GetAxisRaw("Vertical");   // W/S or Up/Down

        Vector3 move = transform.right * h + transform.forward * v;

        // Vertical movement — useful for flying above terrain to scout
        if (Input.GetKey(KeyCode.E)) move += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) move += Vector3.down;

        // Normalise so diagonal isn't faster, then apply
        if (move.sqrMagnitude > 1f) move.Normalize();

        transform.position += move * speed * Time.deltaTime;
    }
}
