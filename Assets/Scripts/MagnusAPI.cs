using UnityEngine;
using System.Collections;

public class MagnusAPI : MonoBehaviour
{
    [Header("IK Control")]
    [SerializeField] Transform magnusTarget;

    [Header("Claw Components")]
    [SerializeField] Transform pointer;
    [SerializeField] Transform thumb;
    [SerializeField] Transform clawCenter; // The point to parent objects to

    [Header("Claw Settings")]
    [Tooltip("How long the grip/release animation takes in seconds.")]
    [SerializeField] float gripDuration = 0.5f;

    [Header("Movement Speed")]
    [Tooltip("The speed of the arm when it is not carrying an object.")]
    [SerializeField] float emptyHandSpeed = 2f;
    [Tooltip("The speed of the arm when it is carrying an object.")]
    [SerializeField] float carryingSpeed = 1f;

    // Define the open and closed rotations for the claw fingers
    private readonly Quaternion pointerOpenRotation = Quaternion.Euler(58, 0, 0);
    private readonly Quaternion thumbOpenRotation = Quaternion.Euler(-58, 0, 0);
    private readonly Quaternion pointerClosedRotation = Quaternion.Euler(18, 0, 0);
    private readonly Quaternion thumbClosedRotation = Quaternion.Euler(-18, 0, 0);

    private Coroutine clawAnimationCoroutine;
    private Coroutine moveObjectCoroutine;
    private Coroutine moveTargetCoroutine;
    private bool isMovingObject = false;

    /// <summary>
    /// Moves the robot arm's target to a specific world position.
    /// </summary>
    /// <param name="targetPosition">The world space position to move to.</param>
    public void MoveTo(Vector3 targetPosition)
    {
        if (isMovingObject) return; // Don't allow manual moves while an automated sequence is running
        if (moveTargetCoroutine != null) StopCoroutine(moveTargetCoroutine);
        moveTargetCoroutine = StartCoroutine(MoveTargetRoutine(targetPosition));
    }

    /// <summary>
    /// Closes the claw.
    /// </summary>
    public void Grip()
    {
        if (clawAnimationCoroutine != null) StopCoroutine(clawAnimationCoroutine);
        clawAnimationCoroutine = StartCoroutine(AnimateClaw(ClawState.Closed));
    }

    /// <summary>
    /// Opens the claw.
    /// </summary>
    public void Release()
    {
        if (clawAnimationCoroutine != null) StopCoroutine(clawAnimationCoroutine);
        clawAnimationCoroutine = StartCoroutine(AnimateClaw(ClawState.Open));
    }

    /// <summary>
    /// Starts the sequence to pick up an object and move it to a new position.
    /// </summary>
    /// <param name="targetObject">The GameObject to move.</param>
    /// <param name="newPosition">The destination world position.</param>
    public void MoveObject(GameObject targetObject, Vector3 newPosition)
    {
        if (isMovingObject)
        {
            Debug.LogWarning("Already moving an object. Please wait for the current sequence to finish.");
            return;
        }
        if (moveObjectCoroutine != null) StopCoroutine(moveObjectCoroutine);
        moveObjectCoroutine = StartCoroutine(MoveObjectRoutine(targetObject, newPosition));
    }

    /// <summary>
    /// The coroutine that handles the multi-step process of moving an object.
    /// </summary>
    private IEnumerator MoveObjectRoutine(GameObject targetObject, Vector3 newPosition)
    {
        isMovingObject = true;

        // 1. Open the claw to prepare for pickup
        yield return StartCoroutine(AnimateClaw(ClawState.Open));

        // 2. Move to the object's current position
        yield return StartCoroutine(MoveTargetRoutine(targetObject.transform.position));

        // 3. Grip the object
        yield return StartCoroutine(AnimateClaw(ClawState.Closed));
        targetObject.transform.SetParent(clawCenter); // Attach object to claw
        // disable gravity on the object if necessary
        Rigidbody rb = targetObject.GetComponent<Rigidbody>();
        bool usingGravity = rb != null && rb.useGravity;
        if (rb != null && usingGravity)
        {
            rb.useGravity = false;
        }

        // 4. Move to the new destination
        yield return StartCoroutine(MoveTargetRoutine(newPosition));

        // 5. Release the object
        yield return StartCoroutine(AnimateClaw(ClawState.Open));
        targetObject.transform.SetParent(null); // Detach object from claw

        if (rb != null && usingGravity)
        {
            rb.useGravity = true; // Re-enable gravity
        }

        isMovingObject = false;
    }
    
    /// <summary>
    /// Coroutine to smoothly move the IK target to a destination.
    /// </summary>
    private IEnumerator MoveTargetRoutine(Vector3 destination)
    {
        Vector3 startPosition = magnusTarget.position;
        float distance = Vector3.Distance(startPosition, destination);
        if (distance < 0.01f) yield break; // Already there, no need to move.

        // Check if holding an object to determine speed.
        bool isCarrying = clawCenter.childCount > 0;
        float speed = isCarrying ? carryingSpeed : emptyHandSpeed;
        
        // If speed is zero or negative, movement is instant.
        if (speed <= 0)
        {
            magnusTarget.position = destination;
            yield break;
        }

        float duration = distance / speed; // Calculate time based on distance and speed
        float elapsedTime = 0;

        while (elapsedTime < duration)
        {
            // Use a smooth step for acceleration and deceleration
            float t = Mathf.SmoothStep(0.0f, 1.0f, elapsedTime / duration);
            magnusTarget.position = Vector3.Lerp(startPosition, destination, t);
            elapsedTime += Time.deltaTime;
            yield return null;
        }
        
        magnusTarget.position = destination; // Snap to final position to ensure accuracy
    }

    private enum ClawState { Open, Closed }

    /// <summary>
    /// Coroutine to smoothly animate the claw opening or closing.
    /// </summary>
    private IEnumerator AnimateClaw(ClawState targetState)
    {
        Quaternion pointerStartRot = pointer.localRotation;
        Quaternion thumbStartRot = thumb.localRotation;

        Quaternion pointerTargetRot = (targetState == ClawState.Open) ? pointerOpenRotation : pointerClosedRotation;
        Quaternion thumbTargetRot = (targetState == ClawState.Open) ? thumbOpenRotation : thumbClosedRotation;

        float elapsedTime = 0f;
        while (elapsedTime < gripDuration)
        {
            float t = elapsedTime / gripDuration;
            pointer.localRotation = Quaternion.Slerp(pointerStartRot, pointerTargetRot, t);
            thumb.localRotation = Quaternion.Slerp(thumbStartRot, thumbTargetRot, t);
            elapsedTime += Time.deltaTime;
            yield return null;
        }

        // Snap to final rotation to ensure accuracy
        pointer.localRotation = pointerTargetRot;
        thumb.localRotation = thumbTargetRot;
    }
}
