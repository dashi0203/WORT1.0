using UnityEngine;
using UnityEngine.InputSystem;

[DisallowMultipleComponent]
[RequireComponent(typeof(CharacterController))]
public sealed class WortPigPlayer : MonoBehaviour
{
    [SerializeField] private InputActionAsset inputActions;
    [SerializeField] private Animator animator;
    [SerializeField, Min(0f)] private float moveSpeed = 4f;
    [SerializeField, Min(0f)] private float turnSpeed = 900f;
    [SerializeField, Min(0f)] private float jumpHeight = 1f;
    [SerializeField, Range(0.1f, 5f), Tooltip("移动时动画的播放倍速（只在走动时生效，站着时动画按原速）。上限 5 倍。")]
    private float walkAnimationSpeed = 1f;

    private const float JumpBufferSeconds = 0.1f;
    private static readonly int Speed = Animator.StringToHash("Speed");
    private CharacterController character;
    private InputActionAsset playerInput;
    private InputAction move;
    private InputAction jump;
    private float verticalSpeed;
    private float jumpBuffer;

    private void Awake()
    {
        character = GetComponent<CharacterController>();
        playerInput = Instantiate(inputActions);
        move = playerInput.FindAction("Player/Move", throwIfNotFound: true);
        jump = playerInput.FindAction("Player/Jump", throwIfNotFound: true);
    }

    private void OnEnable()
    {
        move.Enable();
        jump.Enable();
    }

    private void OnDisable()
    {
        move?.Disable();
        jump?.Disable();
        verticalSpeed = 0f;
        jumpBuffer = 0f;
    }

    private void OnDestroy()
    {
        if (playerInput)
            Destroy(playerInput);
    }

    private void Update()
    {
        Tick(Time.deltaTime);
    }

    private void Tick(float deltaTime)
    {
        Vector2 input = Vector2.ClampMagnitude(move.ReadValue<Vector2>(), 1f);
        Vector3 direction = new Vector3(input.x, 0f, input.y);
        if (direction.sqrMagnitude > 0.001f)
        {
            Quaternion facing = Quaternion.LookRotation(direction);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, facing, turnSpeed * deltaTime);
        }

        if (jump.WasPressedThisFrame())
            jumpBuffer = JumpBufferSeconds;

        float gravity = Physics.gravity.y;
        verticalSpeed = character.isGrounded ? -2f : verticalSpeed + gravity * deltaTime;
        if (character.isGrounded && jumpBuffer > 0f && jumpHeight > 0f)
        {
            verticalSpeed = Mathf.Sqrt(-2f * gravity * jumpHeight);
            jumpBuffer = 0f;
        }
        jumpBuffer = Mathf.Max(0f, jumpBuffer - deltaTime);

        CollisionFlags contacts = character.Move((direction * moveSpeed + Vector3.up * verticalSpeed) * deltaTime);
        if (verticalSpeed > 0f && (contacts & CollisionFlags.Above) != 0)
            verticalSpeed = 0f;
        else if (verticalSpeed < 0f && (contacts & CollisionFlags.Below) != 0)
            verticalSpeed = -2f;

        if (animator)
        {
            Vector3 velocity = character.velocity;
            float planarSpeed = new Vector2(velocity.x, velocity.z).magnitude;
            animator.SetFloat(Speed, planarSpeed);
            animator.speed = planarSpeed > 0.1f ? walkAnimationSpeed : 1f; // 走动时按倍速播放动画
        }
    }
}
