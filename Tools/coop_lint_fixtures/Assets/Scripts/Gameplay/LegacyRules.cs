using UnityEngine;
using UnityEngine.SceneManagement;
// coop_lint fixture: one hit for each of the eight original rules (regression).
public class LegacyRules : MonoBehaviour
{
    void Go()
    {
        GameSession.AddCash(5f, "x", null);
        float f = Random.Range(0f, 1f);
        string id = System.Guid.NewGuid().ToString();
        var t = FindFirstObjectByType<LegacyRules>();
        var c = Truck.Current.speed;
        Time.timeScale = 0.1f;
        if (SceneManager.GetActiveScene().name == "ShopScene") { }
        var me = PlayerController.Local;
    }
}
