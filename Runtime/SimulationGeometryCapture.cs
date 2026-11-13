using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;
using Object = UnityEngine.Object;

namespace Igtap.Tas.Runtime
{
    // Only native transforms, grids and colliders are copied into this private
    // physics scene. Never instantiate a game prefab or a game MonoBehaviour.
    internal sealed class SimulationGeometryCapture : IDisposable
    {
        private readonly Scene _scene;
        private readonly List<Object> _assets = new List<Object>();
        private readonly Dictionary<string, int> _checks = new Dictionary<string, int>();
        private readonly HashSet<string> _failedKinds = new HashSet<string>();
        private readonly JArray _failures = new JArray();
        private int _recovered, _validated, _matched;
        public SimulationGeometryCapture()
        {
            // Scene unloading completes asynchronously; a preview and a debug
            // export may inspect geometry at the same frozen boundary.
            _scene = SceneManager.CreateScene("Tas Studio geometry inspector " + Guid.NewGuid().ToString("N"), new CreateSceneParameters(LocalPhysicsMode.Physics2D));
        }
        public JObject Report => new JObject { ["recovered"]=_recovered, ["validated"]=_validated, ["matched"]=_matched, ["failures"]=_failures };
        public static JObject Read(Collider2D collider)
        {
            var group = new PhysicsShapeGroup2D(); int count = collider.GetShapes(group); var shapes = new JArray();
            for (int i=0; i<count; i++)
            {
                var shape=group.GetShape(i); var vertices=new List<Vector2>(); group.GetShapeVertices(i,vertices);
                shapes.Add(new JObject { ["type"]=shape.shapeType.ToString(), ["radius"]=shape.radius,
                    ["vertices"]=new JArray(vertices.SelectMany(v => new[] {v.x,v.y})),
                    ["useAdjacentStart"]=shape.useAdjacentStart, ["useAdjacentEnd"]=shape.useAdjacentEnd,
                    ["adjacentStart"]=Point(shape.adjacentStart), ["adjacentEnd"]=Point(shape.adjacentEnd) });
            }
            var result=new JObject { ["list"]=shapes };
            return result;
        }
        private static JObject Point(Vector2 p) => new JObject { ["x"]=p.x, ["y"]=p.y };
        private static bool Supported(Collider2D c) => c is BoxCollider2D || c is CircleCollider2D || c is CapsuleCollider2D ||
            c is PolygonCollider2D || c is EdgeCollider2D || c is CompositeCollider2D || c is TilemapCollider2D;
        public void ValidateActive(Collider2D original, JObject expected)
        {
            if (!Supported(original) || !original.isActiveAndEnabled || original.compositeOperation != Collider2D.CompositeOperation.None) return;
            string kind=original.GetType().Name + (expected["list"].Count()==0 ? "/empty" : "/filled");
            _checks.TryGetValue(kind,out int checkedCount); if (checkedCount>=4) return; _checks[kind]=checkedCount+1;
            _validated++;
            try
            {
                var copy=Bake(original);
                if (!JToken.DeepEquals(expected,copy)) { _failedKinds.Add(kind); Failure(original,"Isolated copy differs from the active native geometry."); }
                else _matched++;
            }
            catch (Exception error) { _failedKinds.Add(kind); Failure(original,error.Message); }
        }
        public JObject Recover(Collider2D original)
        {
            if (!Supported(original) || _failedKinds.Any(k => k.StartsWith(original.GetType().Name+"/",StringComparison.Ordinal))) return null;
            try { var result=Bake(original); _recovered++; return result; }
            catch (Exception error) { Failure(original,error.Message); }
            return null;
        }
        public JObject AllCompositeSources(CompositeCollider2D original)
        {
            if (_failedKinds.Any(k => k.StartsWith(original.GetType().Name+"/",StringComparison.Ordinal))) return null;
            try { return Bake(original,true); }
            catch(Exception error) { Failure(original,error.Message); return null; }
        }
        private void Failure(Collider2D original,string reason) => _failures.Add(new JObject { ["id"]=original.GetInstanceID(), ["type"]=original.GetType().Name, ["name"]=original.name, ["reason"]=reason });
        private JObject Bake(Collider2D original,bool allSources=false)
        {
            var transforms=new Dictionary<Transform,Transform>(); GameObject root=null;
            Transform CopyTransform(Transform source)
            {
                if (transforms.TryGetValue(source,out var existing)) return existing;
                var parent=source.parent==null ? null : CopyTransform(source.parent);
                var go=new GameObject(source.name) {hideFlags=HideFlags.HideAndDontSave};
                if (parent==null) { root=go; go.SetActive(false); SceneManager.MoveGameObjectToScene(go,_scene); }
                else go.transform.SetParent(parent,false);
                var t=go.transform; transforms.Add(source,t);
                t.localPosition=source.localPosition; t.localRotation=source.localRotation; t.localScale=source.localScale;
                var grid=source.GetComponent<Grid>();
                if (grid!=null) { var g=go.AddComponent<Grid>(); g.cellSize=grid.cellSize; g.cellGap=grid.cellGap; g.cellLayout=grid.cellLayout; g.cellSwizzle=grid.cellSwizzle; }
                return t;
            }
            try
            {
                var target=CopyTransform(original.transform);
                var body=original.attachedRigidbody ?? original.GetComponentInParent<Rigidbody2D>(true);
                if (body!=null)
                {
                    var b=CopyTransform(body.transform).gameObject.AddComponent<Rigidbody2D>();
                    b.bodyType=body.bodyType; b.gravityScale=0; b.simulated=true;
                }
                Collider2D CopyCollider(Collider2D source)
                {
                    if (!Supported(source)) throw new NotSupportedException("Unrepresented composite source: " + source.GetType().Name);
                    var go=CopyTransform(source.transform).gameObject;
                    if (source is TilemapCollider2D) CopyTiles(source.GetComponent<Tilemap>(),go);
                    var c=(Collider2D)go.AddComponent(source.GetType());
                    c.offset=source.offset; c.isTrigger=source.isTrigger;
                    if (source is BoxCollider2D box) { var b=(BoxCollider2D)c; b.size=box.size; b.edgeRadius=box.edgeRadius; }
                    else if (source is CircleCollider2D circle) ((CircleCollider2D)c).radius=circle.radius;
                    else if (source is CapsuleCollider2D capsule) { var b=(CapsuleCollider2D)c; b.size=capsule.size; b.direction=capsule.direction; }
                    else if (source is PolygonCollider2D polygon) { var p=(PolygonCollider2D)c; p.pathCount=polygon.pathCount; for(int i=0;i<p.pathCount;i++) p.SetPath(i,polygon.GetPath(i)); }
                    else if (source is EdgeCollider2D edge) { var e=(EdgeCollider2D)c; e.points=edge.points; e.edgeRadius=edge.edgeRadius; e.adjacentStartPoint=edge.adjacentStartPoint; e.adjacentEndPoint=edge.adjacentEndPoint; e.useAdjacentStartPoint=edge.useAdjacentStartPoint; e.useAdjacentEndPoint=edge.useAdjacentEndPoint; }
                    else if (source is CompositeCollider2D composite)
                    {
                        var p=(CompositeCollider2D)c; p.geometryType=composite.geometryType; p.generationType=CompositeCollider2D.GenerationType.Manual;
                        p.vertexDistance=composite.vertexDistance; p.offsetDistance=composite.offsetDistance; p.edgeRadius=composite.edgeRadius;
                    }
                    else if (source is TilemapCollider2D tilemap)
                    { var p=(TilemapCollider2D)c; p.extrusionFactor=tilemap.extrusionFactor; p.maximumTileChangeCount=tilemap.maximumTileChangeCount; }
                    return c;
                }
                var result=CopyCollider(original);
                if (original is CompositeCollider2D)
                {
                    if (body==null) throw new NotSupportedException("Composite has no body.");
                    foreach(var source in body.GetComponentsInChildren<Collider2D>(true).Where(c => c!=original &&
                        c.compositeOperation!=Collider2D.CompositeOperation.None && (c.attachedRigidbody ?? c.GetComponentInParent<Rigidbody2D>(true))==body))
                    {
                        var copy=CopyCollider(source); copy.compositeOperation=source.compositeOperation; copy.compositeOrder=source.compositeOrder; copy.enabled=allSources || source.enabled;
                    }
                }
                root.SetActive(true);
                foreach(var tilemap in root.GetComponentsInChildren<TilemapCollider2D>()) tilemap.ProcessTilemapChanges();
                if (result is CompositeCollider2D compositeResult) compositeResult.GenerateGeometry();
                return Read(result);
            }
            finally { if(root!=null) Object.DestroyImmediate(root); }
        }
        private void CopyTiles(Tilemap source,GameObject destination)
        {
            if (source==null) throw new NotSupportedException("TilemapCollider2D has no Tilemap.");
            var bounds=source.cellBounds;
            long cells=(long)bounds.size.x*bounds.size.y*bounds.size.z;
            if(cells>2000000) throw new NotSupportedException("Tilemap exceeds the bounded export size.");
            var map=destination.AddComponent<Tilemap>(); map.orientation=source.orientation; map.orientationMatrix=source.orientationMatrix; map.tileAnchor=source.tileAnchor;
            var original=source.GetTilesBlock(bounds); var tiles=new TileBase[original.Length]; var cache=new Dictionary<(Sprite,Tile.ColliderType),Tile>();
            int index=0;
            foreach(var pos in bounds.allPositionsWithin)
            {
                if(original[index]!=null)
                {
                    // Read resolved native cell data. Do not execute a RuleTile
                    // or another custom asset's callbacks in the inspector.
                    var key=(source.GetSprite(pos),source.GetColliderType(pos));
                    if(!cache.TryGetValue(key,out var tile)) { tile=ScriptableObject.CreateInstance<Tile>(); tile.hideFlags=HideFlags.HideAndDontSave; tile.sprite=key.Item1; tile.colliderType=key.Item2; tile.flags=TileFlags.None; cache.Add(key,tile); _assets.Add(tile); }
                    tiles[index]=tile;
                }
                index++;
            }
            map.SetTilesBlock(bounds,tiles);
            index=0;
            foreach(var pos in bounds.allPositionsWithin) { if(tiles[index++]!=null) map.SetTransformMatrix(pos,source.GetTransformMatrix(pos)); }
        }
        public void Dispose()
        {
            foreach(var asset in _assets) if(asset!=null) Object.DestroyImmediate(asset);
            if(_scene.IsValid() && _scene.isLoaded) SceneManager.UnloadSceneAsync(_scene);
        }
    }
}
