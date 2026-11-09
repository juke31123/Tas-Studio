using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Recharge.ModApi;
using UnityEngine;
using UnityEngine.InputSystem;
namespace Igtap.Tas.Recorder
{
    public sealed class RecorderMod : IRechargeMod
    {
        public string Id=>"Tas.Studio.Recorder";public string DisplayName=>"Tas Studio Gameplay Recorder";public Version Version=>new Version(1,1,1);
        internal static RecorderController Current;
        private RecorderController _controller;private PassiveHooks _hooks;private ContactCapture _contacts;private GameObject _object;private RecorderWindow _window;
        public void OnLoad(IRechargeHost host){AppDomain.CurrentDomain.AssemblyResolve+=Resolve;try{Initialize(host);}catch{OnUnload();throw;}}
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Initialize(IRechargeHost host)
        {
            if(Current!=null)throw new InvalidOperationException("A gameplay recorder is already loaded.");_controller=new RecorderController(host.ModDataDir(Id));Current=_controller;
            _contacts=new ContactCapture(_controller);_controller.Contacts=_contacts;_object=new GameObject("Gameplay Recorder");UnityEngine.Object.DontDestroyOnLoad(_object);_window=_object.AddComponent<RecorderWindow>();_window.Initialize(_controller);
            _hooks=new PassiveHooks(_controller,Hotkeys);host.Log("Gameplay Recorder loaded. Enter gameplay and press F8 to start/stop. F5 shows controls. Normal timing, inputs and saving remain active.");
        }
        private void Hotkeys()
        {
            if(!Application.isFocused || Keyboard.current==null)return;var k=Keyboard.current;if(k.f5Key.wasPressedThisFrame)_window.Visible=!_window.Visible;
            if(k.f8Key.wasPressedThisFrame)try{if(_controller.Recording)_controller.RequestStop();else _controller.RequestStart(_window.CaptureName,_window.Notes);}catch(Exception e){_controller.RuntimeError(e);}
        }
        private static Assembly Resolve(object sender,ResolveEventArgs args)
        {if(new AssemblyName(args.Name).Name!="0Harmony")return null;string path=Path.Combine(Path.GetDirectoryName(typeof(RecorderMod).Assembly.Location),"0Harmony.dll");return File.Exists(path)?Assembly.LoadFrom(path):null;}
        public void OnUnload(){try{_controller?.Dispose();}finally{try{_contacts?.Dispose();}finally{_hooks?.Dispose();if(_object!=null)UnityEngine.Object.Destroy(_object);AppDomain.CurrentDomain.AssemblyResolve-=Resolve;if(ReferenceEquals(Current,_controller))Current=null;}}}
    }
}
