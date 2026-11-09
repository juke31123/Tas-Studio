using System;
using UnityEngine;
namespace Igtap.Tas.Recorder
{
    internal sealed class RecorderWindow : MonoBehaviour
    {
        public bool Visible=true;
        public string CaptureName="gameplay",Notes="";
        private RecorderController _controller;private Rect _rect=new Rect(20,20,410,300);private bool _wasRecording;private string _error="";
        public void Initialize(RecorderController controller)=>_controller=controller;
        private void Update(){if(_controller!=null && _controller.Recording!=_wasRecording){_wasRecording=_controller.Recording;Visible=!_wasRecording;}}
        private void OnGUI()
        {
            if(!Visible || _controller==null)return;float scale=Math.Min(1,Math.Min((Screen.width-24)/410f,(Screen.height-24)/300f));if(scale<=0)return;
            var matrix=GUI.matrix;GUI.matrix=Matrix4x4.Scale(new Vector3(scale,scale,1));try{_rect=GUI.Window(GetInstanceID(),_rect,Draw,"Gameplay Recorder 1.1.0 | F5 show/hide");}finally{GUI.matrix=matrix;}
        }
        private void Draw(int id)
        {
            GUILayout.Label(_controller.Status);GUILayout.Label(_controller.CapturedFrames+" frames | "+_controller.CapturedPus+" physics updates");
            if(_error.Length>0 || _controller.Error.Length>0)GUILayout.Label(_error+_controller.Error);
            if(!_controller.Recording){GUILayout.Label("Name");CaptureName=GUILayout.TextField(CaptureName,35);GUILayout.Label("Notes (optional)");Notes=GUILayout.TextField(Notes,1000);}
            GUI.enabled=_controller.Recording || _controller.CanStart;
            if(GUILayout.Button(_controller.Recording ? "Stop + export (F8)" : "Record current gameplay (F8)"))try{_error="";if(_controller.Recording)_controller.RequestStop();else _controller.RequestStart(CaptureName,Notes);}catch(Exception e){_error=e.Message;}
            GUI.enabled=true;if(GUILayout.Button("Open export folder"))try{_controller.OpenOutput();}catch(Exception e){_error=e.Message;}
            if(GUILayout.Button("Hide (F5)"))Visible=false;GUILayout.Label("Play normally. Use the game's own pause menu.\nSend the *-validation.zip from data/exports.");GUI.DragWindow(new Rect(0,0,410,22));
        }
    }
}
