using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Igtap.Tas.Core
{
    public sealed class CustomMapInfo
    {
        public string Id, Name;
    }

    public static class CustomMapCatalog
    {
        public static void ValidateId(string id)
        {
            if(string.IsNullOrWhiteSpace(id)||id.Length>160||id=="."||id==".."||id.IndexOfAny(new[]{'/','\\',':','\0'})>=0||id.IndexOfAny(Path.GetInvalidFileNameChars())>=0)
                throw new InvalidDataException("Invalid custom map ID.");
        }
        public static CustomMapInfo[] List(string root)
        {
            var result=new List<CustomMapInfo>();
            if(!Directory.Exists(root))return result.ToArray();
            foreach(var directory in Directory.GetDirectories(root))
            {
                string id=Path.GetFileName(directory),file=Path.Combine(directory,"map.json");
                if(!File.Exists(file))continue;
                try
                {
                    ValidateId(id);string name=null;
                    using(var reader=new JsonTextReader(File.OpenText(file)))
                        while(reader.Read())
                        {
                            if(reader.Depth==1&&reader.TokenType==JsonToken.PropertyName&&(string)reader.Value=="name"){name=reader.ReadAsString();break;}
                            if(reader.Depth==1&&reader.TokenType==JsonToken.PropertyName){reader.Read();reader.Skip();}
                        }
                    result.Add(new CustomMapInfo{Id=id,Name=string.IsNullOrWhiteSpace(name)?id:name});
                }
                catch(IOException){}catch(JsonException){}catch(ArgumentException){}
            }
            return result.OrderBy(m=>m.Name,StringComparer.OrdinalIgnoreCase).ThenBy(m=>m.Id,StringComparer.Ordinal).ToArray();
        }
        public static string Fingerprint(string root,string id)
        {
            ValidateId(id);string directory=Path.GetFullPath(Path.Combine(root,id));
            if(!File.Exists(Path.Combine(directory,"map.json")))throw new FileNotFoundException("Install the custom map '"+id+"' in Recharge/Navigator before loading this TAS.");
            // Include the map document and bundled assets; hub metadata does not affect play.
            using var sha=SHA256.Create();using var stream=new CryptoStream(Stream.Null,sha,CryptoStreamMode.Write);
            var paths=new List<string>();var pending=new Stack<string>();pending.Push(directory);
            while(pending.Count>0)
            {
                string next=pending.Pop();if((File.GetAttributes(next)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Custom map folders cannot be links.");
                paths.AddRange(Directory.GetFiles(next));foreach(string child in Directory.GetDirectories(next))pending.Push(child);
            }
            foreach(string path in paths.OrderBy(p=>p,StringComparer.Ordinal))
            {
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Custom map assets cannot be links.");
                string relative=path.Substring(directory.Length+1).Replace('\\','/');if(relative=="hub.json")continue;
                byte[] name=Encoding.UTF8.GetBytes(relative);byte[] length=BitConverter.GetBytes(name.Length);stream.Write(length,0,length.Length);stream.Write(name,0,name.Length);
                using var file=File.OpenRead(path);byte[] size=BitConverter.GetBytes(file.Length);stream.Write(size,0,size.Length);file.CopyTo(stream);
            }
            stream.FlushFinalBlock();return BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();
        }
    }
}
