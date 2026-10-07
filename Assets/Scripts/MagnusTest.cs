using UnityEngine;
using System.Collections;

/// <summary>
/// A test script to demonstrate the functionality of the MagnusAPI.
/// After a delay, it commands the robot arm to pick up an object and move it.
/// </summary>
public class MagnusTest : MonoBehaviour
{
    [Header("API Reference")]
    [Tooltip("The MagnusAPI component in the scene.")]
    [SerializeField] private MagnusAPI magnusAPI;

    [Header("Test Objects")]
    [Tooltip("The object for the robot arm to pick up.")]
    [SerializeField] private GameObject objectToMove;
    
    [Tooltip("An empty transform marking the location to drop the object.")]
    [SerializeField] private Transform dropOffPoint;

    void Start()
    {
        // Basic validation to ensure all required components are assigned in the inspector.
        if (magnusAPI == null)
        {
            Debug.LogError("MagnusAPI reference is not set in the inspector!", this);
            return;
        }
        if (objectToMove == null)
        {
            Debug.LogError("Object To Move is not set in the inspector!", this);
            return;
        }
        if (dropOffPoint == null)
        {
            Debug.LogError("Drop Off Point is not set in the inspector!", this);
            return;
        }

        // Start the test sequence coroutine.
        StartCoroutine(RunTestSequence());
    }

    /// <summary>
    /// The coroutine that runs the test sequence.
    /// </summary>
    private IEnumerator RunTestSequence()
    {
        Debug.Log("Magnus test sequence will begin in 5 seconds...");

        // Wait for 5 seconds before starting.
        yield return new WaitForSeconds(10f);

        Debug.Log("Executing MoveObject command.");

        // Call the API to start the process.
        magnusAPI.MoveObject(objectToMove, dropOffPoint.position);
    }
}
