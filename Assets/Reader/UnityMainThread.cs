using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Allows background threads to dispatch actions back onto the Unity main thread.
/// Add this to the same GameObject as WorldStreamer.
/// Call UnityMainThread.Dispatch(action) from any thread.
/// </summary>
public class UnityMainThread : MonoBehaviour
{
    private static UnityMainThread _instance;
    private readonly Queue<Action> _queue = new Queue<Action>();
    private readonly object _lock = new object();

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
    }

    private void Update()
    {
        lock (_lock)
        {
            while (_queue.Count > 0)
                _queue.Dequeue().Invoke();
        }
    }

    /// <summary>
    /// Dispatches an action to run on the main thread at the next Update.
    /// Safe to call from any thread.
    /// </summary>
    public static void Dispatch(Action action)
    {
        if (_instance == null)
        {
            Debug.LogError("[UnityMainThread] No instance found in scene.");
            return;
        }

        lock (_instance._lock)
            _instance._queue.Enqueue(action);
    }
}
