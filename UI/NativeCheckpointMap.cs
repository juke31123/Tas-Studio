using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.DesktopModel;
using N = Igtap.Tas.UI.NativeMethods;

namespace Igtap.Tas.UI
{
    // A detached map of captured collider outlines. Only queued commands reach
    // Unity; drawing and editing this window never move the player or camera.
    internal sealed class NativeCheckpointMap : IDisposable
    {
        private readonly StudioChannel _channel;
        private readonly IntPtr _owner;
        private readonly bool _visible;
        private readonly N.WindowProc _procedure;
        private readonly NativeTheme _theme = new NativeTheme();
        private readonly Dictionary<int,IntPtr> _controls=new Dictionary<int,IntPtr>();
        private IntPtr _handle;
        private string _className, _signature="", _error="";
        private float _scale=1;
        private double _centerX,_centerY,_zoom=1;
        private int _revision=-1,_selected=-1,_panX,_panY;
        private int _pendingSequence;
        private bool _panning;
        private SearchCheckpoint[] _dragPoints;
        private string _dragSignature;
        private int _dragIndex=-1,_dragX,_dragY;
        private double _dragOffsetX,_dragOffsetY;
        private bool _dragMoved;
        private N.Rect _map;
        internal IntPtr Handle=>_handle;
        public NativeCheckpointMap(StudioChannel channel,IntPtr owner,bool visible)
        { _channel=channel;_owner=owner;_visible=visible;_procedure=Procedure; }
        public void Open()
        {
            if(_handle!=IntPtr.Zero){if(_visible){N.ShowWindow(_handle,9);N.SetForegroundWindow(_handle);}return;}
            if(_className==null)
            {
                _className="TasStudio.Checkpoints."+Guid.NewGuid().ToString("N");
                var wc=new N.WindowClass{Size=(uint)Marshal.SizeOf<N.WindowClass>(),Instance=N.GetModuleHandle(null),
                    Procedure=Marshal.GetFunctionPointerForDelegate(_procedure),Cursor=N.LoadCursor(IntPtr.Zero,new IntPtr(32512)),
                    Background=_theme.Brush(NativeTheme.Back),Name=_className};
                if(N.RegisterClassEx(ref wc)==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            _handle=N.CreateWindowEx(0,_className,"Tas Studio - Checkpoint map",0x02CF0000,
                _visible?unchecked((int)0x80000000):-30000,_visible?unchecked((int)0x80000000):-30000,1120,750,
                _owner,IntPtr.Zero,N.GetModuleHandle(null),IntPtr.Zero);
            if(_handle==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            _scale=Math.Max(1,N.GetDpiForWindow(_handle)/96f);_theme.Scale(_scale);NativeTheme.DarkTitle(_handle);
            Add(1,"BUTTON","Refresh map");Add(2,"BUTTON","Fit goals");Add(3,"BUTTON","Center player");
            Add(4,"STATIC","Click empty space: add | Drag a point: move | Middle drag: pan | Wheel: zoom | Esc: cancel drag");
            Add(5,"LISTBOX","");Add(6,"STATIC","X");Add(7,"EDIT","0");Add(8,"STATIC","Y");Add(9,"EDIT","0");
            Add(10,"STATIC","Radius");Add(11,"EDIT","24");Add(12,"BUTTON","Apply point");
            Add(13,"BUTTON","Move up");Add(14,"BUTTON","Move down");Add(15,"BUTTON","Delete point");
            Add(16,"BUTTON","Clear route");Add(17,"BUTTON","Add current player");
            Add(19,"STATIC","");
            if(View!=null)N.SetWindowText(_controls[11],View.Options.GoalRadius.ToString("R",CultureInfo.InvariantCulture));
            foreach(var h in _controls.Values)N.SendMessage(h,0x30,_theme.Normal,new IntPtr(1));
            _signature="";_revision=-1;Layout();Refresh();Send(new Command{Action="searchMap"});
            if(_visible)N.ShowWindow(_handle,5);
        }
        private void Add(int id,string kind,string text)
        {
            uint style=kind=="LISTBOX"?0x00A00001u:kind=="EDIT"?0x00800080u:kind=="BUTTON"?11u:0;
            var h=N.CreateWindowEx(0,kind,text,0x50010000|style,0,0,1,1,_handle,new IntPtr(id),N.GetModuleHandle(null),IntPtr.Zero);
            if(h==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());_controls[id]=h;
            if(kind=="EDIT")N.SendMessage(h,0xC5,new IntPtr(32),IntPtr.Zero);
        }
        private void Move(int id,int x,int y,int w,int h)=>N.MoveWindow(_controls[id],(int)(x*_scale),(int)(y*_scale),(int)(w*_scale),(int)(h*_scale),true);
        private void Layout()
        {
            if(_handle==IntPtr.Zero||_controls.Count==0)return;
            N.GetClientRect(_handle,out var r);int w=(int)(r.Right/_scale),h=(int)(r.Bottom/_scale),side=w-292;
            _map=new N.Rect{Left=(int)(16*_scale),Top=(int)(92*_scale),Right=(int)((side-16)*_scale),Bottom=(int)((h-52)*_scale)};
            Move(1,16,14,128,32);Move(2,154,14,112,32);Move(3,276,14,140,32);Move(4,16,56,w-32,26);
            Move(5,side,92,276,Math.Max(100,h-396));
            int y=h-292;Move(6,side,y,24,26);Move(7,side+28,y,104,28);Move(8,side+144,y,24,26);Move(9,side+172,y,104,28);
            Move(10,side,y+40,72,26);Move(11,side+76,y+38,74,28);Move(12,side+158,y+38,118,30);
            Move(13,side,y+80,132,30);Move(14,side+144,y+80,132,30);
            Move(15,side,y+120,132,30);Move(16,side+144,y+120,132,30);
            Move(17,side,y+162,276,32);Move(19,16,h-42,w-32,36);
            N.InvalidateRect(_handle,IntPtr.Zero,false);
        }
        private SearchView View=>_channel.Latest?.Search;
        private bool Pending=>(_channel.Latest?.CompletedSequence??0)<_pendingSequence;
        private SearchCheckpoint[] Points=>View?.Options.Checkpoints??Array.Empty<SearchCheckpoint>();
        private SearchCheckpoint[] DrawPoints=>_dragPoints??Points;
        private string Text(int id){var b=new StringBuilder(512);N.GetWindowText(_controls[id],b,b.Capacity);return b.ToString();}
        private float Number(int id)
        { if(!float.TryParse(Text(id).Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var v)||!Movie.Finite(v))throw new ArgumentException("Enter finite coordinates and a radius from 1 to 1000.");return v; }
        private void Send(Command c)
        {
            if(Pending)throw new InvalidOperationException("Wait for the previous checkpoint edit to apply.");
            if(!_channel.Send(c))throw new InvalidOperationException("The Studio command queue is unavailable.");
            if(c.Action=="searchCheckpoints"||c.Action=="searchCheckpointPlayer")_pendingSequence=c.Sequence;
            _error="";
        }
        private void Save(List<SearchCheckpoint> points,int selected)
        {
            if(View==null||View.Busy||View.Preview||Pending)throw new InvalidOperationException("Wait for the current search or checkpoint edit to finish.");
            if(points.Count>64)throw new ArgumentException("Use at most 64 checkpoints.");
            foreach(var p in points)p.Validate();
            _selected=selected;var options=View.Options.Copy();options.Checkpoints=points.ToArray();
            Send(new Command{Action="searchCheckpoints",SearchOptions=options});
        }
        private void SelectPoint()
        {
            if(_selected<0||_selected>=Points.Length)return;
            var p=Points[_selected];N.SetWindowText(_controls[7],p.X.ToString("R",CultureInfo.InvariantCulture));
            N.SetWindowText(_controls[9],p.Y.ToString("R",CultureInfo.InvariantCulture));N.SetWindowText(_controls[11],p.Radius.ToString("R",CultureInfo.InvariantCulture));
        }
        private void Click(int id)
        {
            var points=new List<SearchCheckpoint>(Array.ConvertAll(Points,p=>p.Copy()));
            bool selected=_selected>=0&&_selected<points.Count;
            switch(id)
            {
                case 1:Send(new Command{Action="searchMap"});break;
                case 2:Fit();break;
                case 3:if(View!=null){_centerX=View.Player.X;_centerY=View.Player.Y;}break;
                case 12:if(selected){points[_selected]=new SearchCheckpoint{X=Number(7),Y=Number(9),Radius=Number(11)};Save(points,_selected);}break;
                case 13:case 14:
                    int next=_selected+(id==13?-1:1);
                    if(selected&&next>=0&&next<points.Count){var p=points[_selected];points[_selected]=points[next];points[next]=p;Save(points,next);}break;
                case 15:if(selected){points.RemoveAt(_selected);Save(points,Math.Min(_selected,points.Count-1));}break;
                case 16:Save(new List<SearchCheckpoint>(),-1);break;
                case 17:Send(new Command{Action="searchCheckpointPlayer",Value=Number(11)});break;
            }
            N.InvalidateRect(_handle,IntPtr.Zero,false);
        }
        private double WorldX(int x)=>_centerX+(x-(_map.Left+_map.Right)*.5)/_zoom;
        private double WorldY(int y)=>_centerY-(y-(_map.Top+_map.Bottom)*.5)/_zoom;
        private int ScreenX(double x)=>(int)Math.Max(-100000,Math.Min(100000,(_map.Left+_map.Right)*.5+(x-_centerX)*_zoom));
        private int ScreenY(double y)=>(int)Math.Max(-100000,Math.Min(100000,(_map.Top+_map.Bottom)*.5-(y-_centerY)*_zoom));
        private bool InMap(int x,int y)=>x>=_map.Left&&x<_map.Right&&y>=_map.Top&&y<_map.Bottom;
        public bool ProcessKey(ref N.Message message)
        {
            if(message.Hwnd!=_handle||message.Id!=0x100||message.WParam.ToUInt64()!=27||(_dragIndex<0&&!_panning))return false;
            EndInteraction();return true;
        }
        private void EndInteraction()
        {
            _dragPoints=null;_dragIndex=-1;_dragMoved=false;_panning=false;
            if(N.GetCapture()==_handle)N.ReleaseCapture();
            if(_handle!=IntPtr.Zero)N.InvalidateRect(_handle,IntPtr.Zero,false);
        }
        private void BeginPoint(int x,int y)
        {
            if(View==null||View.Busy||View.Preview||Pending)throw new InvalidOperationException("Wait for the current search or checkpoint edit to finish.");
            int hit=-1;double closest=double.MaxValue;
            for(int i=0;i<Points.Length;i++)
            {
                double dx=x-ScreenX(Points[i].X),dy=y-ScreenY(Points[i].Y),distance=dx*dx+dy*dy;
                double radius=Math.Max(18*_scale,Math.Min(36*_scale,Points[i].Radius*_zoom));
                if(distance<=radius*radius&&distance<closest){hit=i;closest=distance;}
            }
            if(hit<0)
            {
                var list=new List<SearchCheckpoint>(Array.ConvertAll(Points,p=>p.Copy()));
                list.Add(new SearchCheckpoint{X=(float)WorldX(x),Y=(float)WorldY(y),Radius=Number(11)});Save(list,list.Count-1);return;
            }
            _selected=hit;N.SendMessage(_controls[5],0x186,new IntPtr(hit),IntPtr.Zero);SelectPoint();
            _dragIndex=hit;_dragPoints=Array.ConvertAll(Points,p=>p.Copy());_dragSignature=SearchFiles.Hash(Points);
            _dragX=x;_dragY=y;_dragMoved=false;_dragOffsetX=Points[hit].X-WorldX(x);_dragOffsetY=Points[hit].Y-WorldY(y);
            N.SetFocus(_handle);N.SetCapture(_handle);N.InvalidateRect(_handle,IntPtr.Zero,false);
        }
        private void MovePoint(int x,int y)
        {
            if(_dragIndex<0)return;
            if(Math.Abs(x-_dragX)+Math.Abs(y-_dragY)>=3*_scale)_dragMoved=true;
            if(!_dragMoved)return;
            _dragPoints[_dragIndex].X=(float)(WorldX(x)+_dragOffsetX);_dragPoints[_dragIndex].Y=(float)(WorldY(y)+_dragOffsetY);
            N.InvalidateRect(_handle,IntPtr.Zero,false);
        }
        private void DropPoint(int x,int y)
        {
            if(_dragIndex<0)return;MovePoint(x,y);
            var points=_dragPoints;int selected=_dragIndex;
            bool commit=_dragMoved&&InMap(x,y)&&_dragSignature==SearchFiles.Hash(Points);
            EndInteraction();if(commit)Save(new List<SearchCheckpoint>(points),selected);
        }
        private void Fit()
        {
            if(View==null)return;double minX=View.Player.X-128,maxX=View.Player.X+128,minY=View.Player.Y-128,maxY=View.Player.Y+128;
            foreach(var p in Points){minX=Math.Min(minX,p.X-p.Radius);maxX=Math.Max(maxX,p.X+p.Radius);minY=Math.Min(minY,p.Y-p.Radius);maxY=Math.Max(maxY,p.Y+p.Radius);}
            _centerX=(minX+maxX)*.5;_centerY=(minY+maxY)*.5;
            _zoom=Math.Max(.001,Math.Min(20,Math.Min((_map.Right-_map.Left)/Math.Max(1,maxX-minX),(_map.Bottom-_map.Top)/Math.Max(1,maxY-minY))*.85));
        }
        public void Refresh()
        {
            if(_handle==IntPtr.Zero||View==null)return;
            string signature=SearchFiles.Hash(Points);
            if(signature!=_signature)
            {
                if(_dragIndex>=0)EndInteraction();
                _signature=signature;N.SendMessage(_controls[5],0x184,IntPtr.Zero,IntPtr.Zero);
                for(int i=0;i<Points.Length;i++)N.SendText(_controls[5],0x180,IntPtr.Zero,(i+1)+". "+Points[i].X.ToString("0.#",CultureInfo.InvariantCulture)+", "+Points[i].Y.ToString("0.#",CultureInfo.InvariantCulture)+"  r="+Points[i].Radius.ToString("0.#",CultureInfo.InvariantCulture));
                _selected=Math.Max(Points.Length==0?-1:0,Math.Min(_selected,Points.Length-1));
                N.SendMessage(_controls[5],0x186,new IntPtr(_selected),IntPtr.Zero);SelectPoint();N.InvalidateRect(_handle,IntPtr.Zero,false);
            }
            if(View.MapRevision>0&&_revision!=View.MapRevision){bool first=_revision<0;_revision=View.MapRevision;if(first)Fit();N.InvalidateRect(_handle,IntPtr.Zero,false);}
            bool editable=!View.Busy&&!View.Preview&&!Pending;
            if(!editable&&_dragIndex>=0)EndInteraction();
            foreach(int id in new[]{7,9,11,12,13,14,15,16,17})N.EnableWindow(_controls[id],editable);
            string status=_error.Length>0?_error:Pending?"Applying checkpoint edit...":Points.Length==0?"Click the map to place the finish. Solid: blue | Hazard: red | Other: amber":
                Points.Length+" checkpoints, required in order. Final point is the finish. Drag numbered points to move them.";
            // Avoid invalidating a STATIC control on every 100 ms refresh.
            if(Text(19)!=status)N.SetWindowText(_controls[19],status);
        }
        private IntPtr Procedure(IntPtr hwnd,uint message,IntPtr wp,IntPtr lp)
        {
            try
            {
                int x=(short)(lp.ToInt64()&0xffff),y=(short)((lp.ToInt64()>>16)&0xffff);
                switch(message)
                {
                    case 0x10:EndInteraction();N.DestroyWindow(hwnd);return IntPtr.Zero;
                    case 0x2:_handle=IntPtr.Zero;_controls.Clear();return IntPtr.Zero;
                    case 0x5:Layout();return IntPtr.Zero;
                    case 0x24:var mm=Marshal.PtrToStructure<N.MinMax>(lp);mm.MinTrack=new N.Point{X=(int)(900*_scale),Y=(int)(660*_scale)};Marshal.StructureToPtr(mm,lp,false);return IntPtr.Zero;
                    case 0x2E0:_scale=(wp.ToInt64()&0xffff)/96f;_theme.Scale(_scale);foreach(var h in _controls.Values)N.SendMessage(h,0x30,_theme.Normal,new IntPtr(1));var r=Marshal.PtrToStructure<N.Rect>(lp);N.MoveWindow(hwnd,r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top,true);Layout();return IntPtr.Zero;
                    case 0x111:
                        int id=(int)(wp.ToInt64()&0xffff),code=(int)((wp.ToInt64()>>16)&0xffff);
                        if(id==5&&code==1){_selected=N.SendMessage(_controls[5],0x188,IntPtr.Zero,IntPtr.Zero).ToInt32();SelectPoint();N.InvalidateRect(hwnd,IntPtr.Zero,false);}
                        else if(code==0)Click(id);return IntPtr.Zero;
                    case 0x201:if(InMap(x,y)&&!_panning)BeginPoint(x,y);return IntPtr.Zero;
                    case 0x202:DropPoint(x,y);return IntPtr.Zero;
                    case 0x204:case 0x207:if(InMap(x,y)&&_dragIndex<0){_panning=true;_panX=x;_panY=y;N.SetCapture(hwnd);}return IntPtr.Zero;
                    case 0x205:case 0x208:if(_panning)EndInteraction();return IntPtr.Zero;
                    case 0x215:case 0x1F:EndInteraction();return IntPtr.Zero;
                    case 0x100:if(wp.ToInt32()==27){EndInteraction();return IntPtr.Zero;}break;
                    case 0x200:if(_panning){_centerX-=(x-_panX)/_zoom;_centerY+=(y-_panY)/_zoom;_panX=x;_panY=y;N.InvalidateRect(hwnd,IntPtr.Zero,false);}else MovePoint(x,y);return IntPtr.Zero;
                    case 0x20A:if(_dragIndex<0){_zoom=Math.Max(.001,Math.Min(100,_zoom*Math.Pow(1.2,(short)((wp.ToInt64()>>16)&0xffff)/120.0)));N.InvalidateRect(hwnd,IntPtr.Zero,false);}return IntPtr.Zero;
                    case 0x2B:_theme.Button(Marshal.PtrToStructure<N.DrawItem>(lp));return new IntPtr(1);
                    case 0x133:case 0x134:case 0x135:case 0x138:uint c=message==0x133||message==0x134?NativeTheme.Input:NativeTheme.Back;N.SetBkColor(wp,c);N.SetTextColor(wp,NativeTheme.Text);return _theme.Brush(c);
                    case 0x14:return new IntPtr(1);
                    case 0xF:Paint(hwnd);return IntPtr.Zero;
                }
            }
            catch(Exception e){_error=e.Message;if(_controls.ContainsKey(19))N.SetWindowText(_controls[19],_error);}
            return N.DefWindowProc(hwnd,message,wp,lp);
        }
        private void Paint(IntPtr hwnd)
        {
            var dc=N.BeginPaint(hwnd,out var paint);IntPtr memory=IntPtr.Zero,bitmap=IntPtr.Zero,oldBitmap=IntPtr.Zero;
            try
            {
                N.GetClientRect(hwnd,out var area);if(area.Right<1||area.Bottom<1)return;
                memory=N.CreateCompatibleDC(dc);bitmap=N.CreateCompatibleBitmap(dc,area.Right,area.Bottom);oldBitmap=N.SelectObject(memory,bitmap);
                N.FillRect(memory,ref area,_theme.Brush(NativeTheme.Back));var map=_map;N.FillRect(memory,ref map,_theme.Brush(NativeTheme.Input));
                int saved=N.SaveDC(memory);N.IntersectClipRect(memory,map.Left,map.Top,map.Right,map.Bottom);
                try
                {
                    foreach(var body in View?.Geometry??Array.Empty<SearchBody>())
                    {
                        if(body.MaxX<WorldX(map.Left)||body.MinX>WorldX(map.Right)||body.MaxY<WorldY(map.Bottom)||body.MinY>WorldY(map.Top))continue;
                        uint color=body.Kind=="solid"?NativeTheme.Blue:body.Kind=="hazard"?NativeTheme.Red:NativeTheme.Amber;
                        DrawOutline(memory,body,color);
                    }
                    var points=DrawPoints;
                    for(int i=0;i<points.Length;i++)
                    {
                        var p=points[i];if(i>0)Line(memory,ScreenX(points[i-1].X),ScreenY(points[i-1].Y),ScreenX(p.X),ScreenY(p.Y),NativeTheme.Muted);
                        int px=ScreenX(p.X),py=ScreenY(p.Y),radius=(int)Math.Min(100000,Math.Max(4,p.Radius*_zoom));
                        Circle(memory,px,py,radius,i==_selected?NativeTheme.Amber:NativeTheme.Green);
                        var label=new N.Rect{Left=px-18,Top=py-13,Right=px+18,Bottom=py+13};_theme.Box(memory,label,NativeTheme.Back,5);
                        _theme.Label(memory,label,(i+1).ToString(),NativeTheme.Text,_theme.Normal,0x8825);
                    }
                    if(View!=null){int px=ScreenX(View.Player.X),py=ScreenY(View.Player.Y);Line(memory,px-7,py,px+7,py,NativeTheme.Accent);Line(memory,px,py-7,px,py+7,NativeTheme.Accent);}
                }
                finally{N.RestoreDC(memory,saved);}
                N.BitBlt(dc,0,0,area.Right,area.Bottom,memory,0,0,0x00CC0020);
            }
            finally{if(oldBitmap!=IntPtr.Zero)N.SelectObject(memory,oldBitmap);if(bitmap!=IntPtr.Zero)N.DeleteObject(bitmap);if(memory!=IntPtr.Zero)N.DeleteDC(memory);N.EndPaint(hwnd,ref paint);}
        }
        private void DrawOutline(IntPtr dc,SearchBody body,uint color)
        {
            if(body.Vertices.Length>0){for(int i=0;i<body.Vertices.Length;i++){var a=body.Vertices[i];var b=body.Vertices[(i+1)%body.Vertices.Length];Line(dc,ScreenX(a.X),ScreenY(a.Y),ScreenX(b.X),ScreenY(b.Y),color);}}
            else{int x=ScreenX(body.MinX),y=ScreenY(body.MaxY),right=ScreenX(body.MaxX),bottom=ScreenY(body.MinY);Line(dc,x,y,right,y,color);Line(dc,right,y,right,bottom,color);Line(dc,right,bottom,x,bottom,color);Line(dc,x,bottom,x,y,color);}
        }
        private static void Line(IntPtr dc,int x,int y,int x2,int y2,uint color)
        {var pen=N.CreatePen(0,1,color);var old=N.SelectObject(dc,pen);N.MoveToEx(dc,x,y,IntPtr.Zero);N.LineTo(dc,x2,y2);N.SelectObject(dc,old);N.DeleteObject(pen);}
        private static void Circle(IntPtr dc,int x,int y,int radius,uint color)
        {var pen=N.CreatePen(0,2,color);var old=N.SelectObject(dc,pen);var brush=N.SelectObject(dc,N.GetStockObject(5));N.Ellipse(dc,x-radius,y-radius,x+radius,y+radius);N.SelectObject(dc,brush);N.SelectObject(dc,old);N.DeleteObject(pen);}
        public void Dispose(){if(_handle!=IntPtr.Zero)N.DestroyWindow(_handle);if(_className!=null){N.UnregisterClass(_className,N.GetModuleHandle(null));_className=null;}_theme.Dispose();}
    }
}
