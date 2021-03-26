﻿using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Text;
using System;
using Ubiq.Networking;
using UnityEngine.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using System.Linq;

namespace Ubiq.Messaging
{
    public class NetworkContext
    {
        public NetworkScene scene;
        public INetworkObject networkObject;
        public ushort componentId;

        public void Send(ReferenceCountedSceneGraphMessage message)
        {
            message.objectid = networkObject.Id;
            message.componentid = componentId;
            scene.Send(message.buffer);
        }

        public void Send(NetworkId objectid, ushort componentid, string message)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            var msg = ReferenceCountedSceneGraphMessage.Rent(bytes.Length);
            Array.Copy(bytes, 0, msg.bytes, msg.start, bytes.Length);
            msg.componentid = componentid;
            msg.objectid = objectid;
            scene.Send(msg.buffer);
        }

        public void Send(string message)
        {
            Send(networkObject.Id, componentId, message);
        }

        public void SendJson<T>(NetworkId objectid, T message)
        {
            Send(objectid, componentId, JsonUtility.ToJson(message));
        }

        public void SendJson<T>(T message)
        {
            SendJson(networkObject.Id, message);
        }
    }

    /// <summary>
    /// The Network Scene connects networked Components to Network Connections.
    /// </summary>
    public class NetworkScene : MonoBehaviour, IDisposable, INetworkObject
    {
        private List<INetworkConnection> connections = new List<INetworkConnection>();
        private List<Action> actions = new List<Action>();
        private HashSet<string> existingIdAssignments = new HashSet<string>();

        public T GetNetworkComponent<T>() where T : class
        {
            foreach (var networkObject in objectProperties.Select(p => p.Value))
            {
                foreach (var component in networkObject.components)
                {
                    if(component is T)
                    {
                        return component as T;
                    }
                }
            }
            return null;
        }

        private class ObjectProperties
        {
            public NetworkScene scene;
            public INetworkObject identity;
            public Dictionary<int, INetworkComponent> components = new Dictionary<int, INetworkComponent>();
        }

        private Dictionary<INetworkObject, ObjectProperties> objectProperties = new Dictionary<INetworkObject, ObjectProperties>();

        public NetworkId Id { get; } = NetworkScene.GenerateUniqueId();

        private static NetworkScene rootNetworkScene;

        private void Awake()
        {
            if (transform.parent == null)
            {
                if (rootNetworkScene == null)
                {
                    rootNetworkScene = this;
                    DontDestroyOnLoad(gameObject);
                }
                else // Only one networkscene can exist at the top level of the hierarchy
                {
                    gameObject.SetActive(false); // Deactivate the branch to avoid Start() being called until the branch is destroyed
                    Destroy(gameObject);
                }
            }
            else // the network scene is in a forest
            {
                foreach (var item in GetComponents<NetworkScene>())
                {
                    if(item != this)
                    {
                        Destroy(item);
                    }
                }
            }
        }

        /// <summary>
        /// Add a networked component to the network client. Once registered the object can be moved around.
        /// </summary>
        public static NetworkContext Register(INetworkComponent component)
        {
            return FindNetworkScene(component as MonoBehaviour).RegisterComponent(component);
        }

        public static NetworkScene FindNetworkScene(MonoBehaviour component)
        {
            var scene = component.GetComponentInParent<NetworkScene>();
            if (scene)
            {
                return scene;
            }
            if(rootNetworkScene != null)
            {
                return rootNetworkScene;
            }

            var queue = new Queue<Transform>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                foreach(var root in SceneManager.GetSceneAt(i).GetRootGameObjects())
                {
                    queue.Enqueue(root.transform);
                }
            }

            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (next.GetComponent<NetworkScene>())
                {
                    return next.GetComponent<NetworkScene>();
                }
                foreach (Transform child in next)
                {
                    queue.Enqueue(child);
                }
            }

            return null;
        }

        public static ushort GetComponentId<T>()
        {
            return typeof(T).GetComponentId();
        }

        public static ushort GetComponentId(INetworkComponent component)
        {
            return component.GetType().GetComponentId();
        }

        public NetworkContext RegisterComponent(INetworkComponent component)
        {
            INetworkObject networkObject = null;

            if(component is INetworkObject)
            {
                networkObject = component as INetworkObject;
            }
            else
            {
                foreach (var item in (component as MonoBehaviour).GetComponentsInParent<MonoBehaviour>()) // search up
                {
                    if(item is INetworkObject)
                    {
                        networkObject = item as INetworkObject;
                        break;
                    }
                }
            }

            actions.Add((Action)(() => // this may be called while iterating over objectproperties, so register it to execute when outside of the iterator
            {
                if (!objectProperties.ContainsKey(networkObject))
                {
                    objectProperties.Add(networkObject, new ObjectProperties()
                    {
                        identity = networkObject,
                        scene = this,
                    });
                }
                objectProperties[networkObject].components[GetComponentId(component)] = component;
            }));

            NetworkContext context = new NetworkContext();
            context.scene = this;
            context.networkObject = networkObject;
            context.componentId = GetComponentId(component);

            return context;
        }

        /// <summary>
        /// Sets the Network Object Id so that it will be consistent for this particular GameObject instance across
        /// different processes.
        /// This is currently based on the Name. There cannot be two objects with the same directory registered.
        /// </summary>
        /// <param name="monoBehaviour"></param>
        public static NetworkId ObjectIdFromName(INetworkObject monoBehaviour)
        {
            return (monoBehaviour as MonoBehaviour).GetComponentInParent<NetworkScene>().GetObjectIdFromName(monoBehaviour);
        }

        public static NetworkId GenerateUniqueId()
        {
            return IdGenerator.GenerateUnique();
        }

        public NetworkId GetObjectIdFromName(INetworkObject monoBehaviour)
        {
            var target = monoBehaviour as MonoBehaviour;
            var key = target.name;
            if(existingIdAssignments.Contains(key))
            {
                throw new Exception("Cannot add multiple objects of the same name.");
            }
            existingIdAssignments.Add(key);
            return IdGenerator.GenerateFromName(key);
        }

        public void AddConnection(INetworkConnection connection)
        {
            connections.Add(connection);
        }

        private void Update()
        {
            ReceiveConnectionMessages();
            foreach (var action in actions)
            {
                action();
            }
            actions.Clear();
        }

        /// <summary>
        /// This checks all connections for messages and fans them out into the individual recieve queues
        /// </summary>
        public void ReceiveConnectionMessages()
        {
            ReferenceCountedMessage m;
            foreach(var c in connections)
            {
                do
                {
                    m = c.Receive();
                    if(m != null)
                    {
                        try
                        {
                            var sgbmessage = new ReferenceCountedSceneGraphMessage(m);
                            foreach (var item in objectProperties)
                            {
                                // Checks if the Unity object has been destroyed. When Unity objects are destroyed, the managed reference can remain around, but the object is invalid. Unity overrides the truth check to indicate this.

                                if (item.Key is UnityEngine.Object)
                                {
                                    if(!(item.Key as UnityEngine.Object))
                                    {
                                        actions.Add(() =>
                                        {
                                            try
                                            {
                                                objectProperties.Remove(item.Key);
                                            }
                                            catch (KeyNotFoundException)
                                            {
                                            }
                                        });
                                        continue;
                                    }
                                }

                                if (item.Key.Id == sgbmessage.objectid)
                                {
                                    INetworkComponent component = null;

                                    try
                                    {
                                        component = item.Value.components[sgbmessage.componentid];
                                    }
                                    catch(KeyNotFoundException)
                                    {
                                        continue;
                                    }

                                    try
                                    {
                                        Profiler.BeginSample("Component Message Processing " + component.ToString());
                                        component.ProcessMessage(sgbmessage);
                                    }
                                    catch(MissingReferenceException e)
                                    {
                                        if(component is UnityEngine.Object)
                                        {
                                            if(!(component as UnityEngine.Object))
                                            {
                                                item.Value.components.Remove(sgbmessage.componentid);
                                                return;
                                            }
                                        }

                                        throw e;
                                    }
                                    finally
                                    {
                                        Profiler.EndSample();
                                    }
                                }
                            }
                        }
                        catch(Exception e)
                        {
                            Debug.LogException(e); // because otherwise this will not be visible to the main thread
                        }
                        finally
                        {
                            m.Release();
                        }
                    }
                    else
                    {
                        break;
                    }
                } while (true);
            }
        }

        public void Send(ReferenceCountedMessage m)
        {
            Profiler.BeginSample("Send");

            foreach (var c in connections)
            {
                m.Acquire();
                c.Send(m);
            }
            m.Release(); // m should have been acquired once on creation.

            Profiler.EndSample();
        }

        private void OnDestroy()
        {
            Dispose();
        }

        public void Dispose()
        {
            foreach(var c in connections)
            {
                try
                {
                    c.Dispose();
                }
                catch
                {

                }
            }
            connections.Clear();
        }
    }

    public class NetworkComponentId : Attribute
    {
        public NetworkComponentId(Type type, ushort id)
        {
            this.type = type;
            this.id = id;
        }

        // The type member serves only as a safety check, in the case that new users copy an existing class with an override and do not realise they need to change the id to avoid conflicts.
        private Type type;
        private ushort id;

        public ushort GetComponentId(Type other)
        {
            if(other != type)
            {
                throw new Exception("NetworkComponentId Attribute is attached to an object with a different type than was declared. Did you copy the attribute and forget to change the class and Id?");
            }
            return id;
        }
    }

    public static class ComponentIdExtensions
    {
        public static ushort GetComponentId(this Type type)
        {
            if(cache.ContainsKey(type))
            {
                return cache[type];
            }

            var idattributes = type.GetCustomAttributes(typeof(NetworkComponentId), true);
            if (idattributes.Length > 0)
            {
                var id = (idattributes[0] as NetworkComponentId).GetComponentId(type);
                cache.Add(type, id);
                return id;
            }
            else
            {
                var id = type.FullName.GetPortableHashCode();
                cache.Add(type, id);
                return id;
            }
        }

        private static Dictionary<Type, ushort> cache = new Dictionary<Type, ushort>();
    }
}