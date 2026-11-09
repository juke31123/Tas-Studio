// Presentation surfaces used by the original pause-menu body. The menu's
// gameplay state and timeScale changes still execute in the game assembly.
namespace UnityEngine
{
    public static partial class PlayerPrefs
    {
        // This preference controls cursor presentation only. Never fall back
        // to the worker machine's preferences for any gameplay setting.
        public static int GetInt(string key)
        { if (key == "Fullscreen") return 0; throw new System.NotSupportedException("Unexported preference: " + key); }
    }
    public enum CursorLockMode { None, Locked, Confined }
    public static class Cursor
    {
        public static CursorLockMode lockState { set { } }
        public static bool visible { set { } }
    }
}
namespace UnityEngine.EventSystems
{
    public class EventSystem : UnityEngine.MonoBehaviour
    {
        [System.ThreadStatic] private static EventSystem _current;
        public static EventSystem current => _current ??= new EventSystem();
        public void SetSelectedGameObject(UnityEngine.GameObject selected) { }
    }
}
