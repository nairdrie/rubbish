using UnityEngine;

/// <summary>
/// Controls the Magnus robot arm using Inverse Kinematics (IK) to reach a target.
/// The arm hierarchy should be: magnus -> basePivot -> shoulder -> elbow -> wrist -> ClawCenter.
/// The script should be attached to the root 'magnus' GameObject.
/// </summary>
public class MagnusIKController : MonoBehaviour
{
    [Header("Arm Components")]
    [Tooltip("The transform that rotates the entire arm horizontally.")]
    public Transform basePivot;

    [Tooltip("The shoulder joint, which rotates up and down.")]
    public Transform shoulder;

    [Tooltip("The elbow joint, which rotates up and down.")]
    public Transform elbow;
    
    [Tooltip("The wrist joint, which rotates up and down.")]
    public Transform wrist;

    [Tooltip("The endpoint of the arm, where the claw is.")]
    public Transform ClawCenter;

    [Header("Target")]
    [Tooltip("The target GameObject for the claw to reach.")]
    public Transform MagnusTarget;

    [Header("Movement Smoothing")]
    [Tooltip("How quickly the arm moves to its target rotation. Higher is faster.")]
    public float lerpSpeed = 10f;

    // Lengths of the arm segments
    private float upperArmLength;
    private float forearmLength;
    private float clawLength;

    // Target rotations calculated in SolveIK
    private Quaternion targetBaseRotation;
    private Quaternion targetShoulderRotation;
    private Quaternion targetElbowRotation;
    private Quaternion targetWristRotation;

    void Start()
    {
        // Calculate the lengths of the arm segments at the start.
        // This assumes the arm starts in a straight, outstretched pose.
        if (shoulder != null && elbow != null && wrist != null && ClawCenter != null)
        {
            upperArmLength = Vector3.Distance(shoulder.position, elbow.position);
            forearmLength = Vector3.Distance(elbow.position, wrist.position);
            clawLength = Vector3.Distance(wrist.position, ClawCenter.position);
        }
        else
        {
            Debug.LogError("One or more arm components are not assigned in the inspector!");
        }

        // Initialize target rotations to current rotations to avoid snapping on start
        if(basePivot != null) targetBaseRotation = basePivot.rotation;
        if(shoulder != null) targetShoulderRotation = shoulder.localRotation;
        if(elbow != null) targetElbowRotation = elbow.localRotation;
        if(wrist != null) targetWristRotation = wrist.localRotation;
    }

    void Update()
    {
        if (MagnusTarget == null || basePivot == null || shoulder == null || elbow == null || wrist == null)
        {
            // Do nothing if the target or arm parts aren't set.
            return;
        }

        SolveIK();
    }

    void LateUpdate()
    {
        // Smoothly interpolate towards the target rotations in LateUpdate for smoother visuals.
        basePivot.rotation = Quaternion.Slerp(basePivot.rotation, targetBaseRotation, Time.deltaTime * lerpSpeed);
        shoulder.localRotation = Quaternion.Slerp(shoulder.localRotation, targetShoulderRotation, Time.deltaTime * lerpSpeed);
        elbow.localRotation = Quaternion.Slerp(elbow.localRotation, targetElbowRotation, Time.deltaTime * lerpSpeed);
        wrist.localRotation = Quaternion.Slerp(wrist.localRotation, targetWristRotation, Time.deltaTime * lerpSpeed);
    }

    /// <summary>
    /// Calculates and applies the IK solution for the arm.
    /// </summary>
    private void SolveIK()
    {
        // --- 0. Initial Setup & Out-of-Reach Check ---
        float totalArmLength = upperArmLength + forearmLength + clawLength;
        Vector3 targetDirection = MagnusTarget.position - basePivot.position;
        targetDirection.y = 0;

        if (Vector3.Distance(basePivot.position, MagnusTarget.position) > totalArmLength)
        {
            // Target is too far, so we just stretch the arm out towards it.
            Vector3 shoulderToTarget = MagnusTarget.position - shoulder.position;
            float angle = Vector3.SignedAngle(targetDirection, shoulderToTarget, basePivot.right);
            targetShoulderRotation = Quaternion.Euler(angle, 0, 0);
            targetElbowRotation = Quaternion.identity;
            targetWristRotation = Quaternion.identity;
            return;
        }

        // --- 1. Base Rotation (Yaw) ---
        if (targetDirection != Vector3.zero)
        {
            targetBaseRotation = Quaternion.LookRotation(targetDirection) * Quaternion.Euler(0, 180, 0);
        }

        // --- 2. Determine Wrist Target Position ---
        // We solve a 2-link IK for the shoulder/elbow by first finding where the wrist should be.
        // Goal 1: Try to place the wrist directly above the target, so the claw points down.
        Vector3 wristTargetPosition = MagnusTarget.position + Vector3.up * clawLength;
        float shoulderToWristDist = Vector3.Distance(shoulder.position, wristTargetPosition);

        // If the wrist target is unreachable with the first two segments, we need a new goal.
        if (shoulderToWristDist > upperArmLength + forearmLength)
        {
            // Goal 2: The arm must stretch. Place the wrist on the line between the shoulder and the main target.
            Vector3 shoulderToMainTargetDir = (MagnusTarget.position - shoulder.position).normalized;
            wristTargetPosition = MagnusTarget.position - shoulderToMainTargetDir * clawLength;
        }
        
        // --- 3. Shoulder and Elbow Rotation (2-Link IK) ---
        // Now solve for the shoulder and elbow to reach the calculated wristTargetPosition.
        float targetDistance = Vector3.Distance(shoulder.position, wristTargetPosition);

        float cosElbowAngle = Mathf.Clamp((Mathf.Pow(upperArmLength, 2) + Mathf.Pow(forearmLength, 2) - Mathf.Pow(targetDistance, 2)) /
                              (2 * upperArmLength * forearmLength), -1.0f, 1.0f);
        float elbowAngleRad = Mathf.Acos(cosElbowAngle);
        float elbowAngleDeg = elbowAngleRad * Mathf.Rad2Deg;
        targetElbowRotation = Quaternion.Euler(-(180 - elbowAngleDeg), 0, 0);

        Vector3 shoulderToWristDir = wristTargetPosition - shoulder.position;
        float angleToTargetDeg = Vector3.SignedAngle(targetDirection, shoulderToWristDir, basePivot.right);

        float asinValue = Mathf.Clamp((forearmLength * Mathf.Sin(elbowAngleRad)) / targetDistance, -1.0f, 1.0f);
        float shoulderAngleOffsetRad = Mathf.Asin(asinValue);
        float shoulderAngleOffsetDeg = shoulderAngleOffsetRad * Mathf.Rad2Deg;
        targetShoulderRotation = Quaternion.Euler(angleToTargetDeg + shoulderAngleOffsetDeg, 0, 0);

        // --- 4. Wrist Rotation ---
        // Finally, calculate the wrist rotation to point the claw at the main target.
        // We do this by finding the angle between the forearm's direction and the desired claw direction.
        Vector3 forearmDirection = (wrist.position - elbow.position).normalized;
        Vector3 clawDirection = (MagnusTarget.position - wrist.position).normalized;

        if (forearmDirection != Vector3.zero && clawDirection != Vector3.zero)
        {
            float wristAngle = Vector3.SignedAngle(forearmDirection, clawDirection, wrist.right);
            targetWristRotation = Quaternion.Euler(wristAngle, 0, 0);
        }
    }
}
