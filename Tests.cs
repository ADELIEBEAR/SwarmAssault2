// SPDX-License-Identifier: MIT
// Developer-only executable. No keyboard/mouse automation and no live game profile.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SwarmDistribution;

class Tests
{
    static int passed; static string work, stub, assembly; static Config config; static string privateXml;
    static void OK(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS " + name); }
    static void Throws(Action action, string name) { try { action(); } catch { OK(true,name); return; } throw new Exception("FAIL: " + name); }
    static string SignedUpdate(Update update)
    {
        byte[] payload = Encoding.UTF8.GetBytes(Core.Json.Serialize(update));
        using (var rsa = new RSACryptoServiceProvider()) { rsa.PersistKeyInCsp=false; rsa.FromXmlString(privateXml); return Core.Json.Serialize(new Signed { Payload=Convert.ToBase64String(payload), Signature=Convert.ToBase64String(rsa.SignData(payload,CryptoConfig.MapNameToOID("SHA256"))) }); }
    }
    static Update Info(string version, byte[] bytes)
    {
        string hash; using(var f=new MemoryStream(bytes)) hash=Core.Hash(f);
        return new Update { Schema=1, Product=Core.Product, Repository=config.Repository, Version=version, FileName="SwarmAssaultII-Setup-v"+version+".exe", Length=bytes.Length, Sha256=hash };
    }
    static string Fixture(string version, string extra, bool badHash)
    {
        string file=Path.Combine(work,"fixture-"+Guid.NewGuid().ToString("N")+".exe");
        var files=new Dictionary<string,byte[]> {
            {"SwarmAssaultII.exe",File.ReadAllBytes(assembly)},
            {"distribution.json",Encoding.UTF8.GetBytes(Core.Json.Serialize(config))},
            {"versions/"+version+"/OpenSA.exe",File.ReadAllBytes(stub)},
            {"versions/"+version+"/content-catalog.json",Encoding.UTF8.GetBytes("[]")}
        };
        if(extra!=null) files.Add(extra,Encoding.UTF8.GetBytes("bad path"));
        var package=new Package {Schema=1,Product=Core.Product,Version=version,Files=new List<Entry>()};
        foreach(var kv in files) using(var f=new MemoryStream(kv.Value)) package.Files.Add(new Entry {Path=kv.Key,Length=kv.Value.Length,Sha256=badHash?new string('0',64):Core.Hash(f)});
        byte[] zip;
        using(var stream=new MemoryStream()) { using(var z=new ZipArchive(stream,ZipArchiveMode.Create,true)) { foreach(var kv in files) using(var f=z.CreateEntry(kv.Key).Open()) f.Write(kv.Value,0,kv.Value.Length); byte[] b=Encoding.UTF8.GetBytes(Core.Json.Serialize(package)); using(var f=z.CreateEntry("package.json").Open()) f.Write(b,0,b.Length); } zip=stream.ToArray(); }
        byte[] prefix=File.ReadAllBytes(assembly);
        using(var output=File.Create(file)) using(var b=new BinaryWriter(output)) { b.Write(prefix); b.Write(zip); b.Write(Encoding.ASCII.GetBytes("SIISET01")); b.Write((long)prefix.Length); b.Write((long)zip.Length); using(var s=SHA256.Create()) b.Write(s.ComputeHash(zip)); }
        return file;
    }
    class Handler:HttpMessageHandler
    {
        public byte[] Body; public string Url;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) { Url=request.RequestUri.AbsoluteUri; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Body)}); }
    }
    static async Task Network()
    {
        byte[] bytes=new byte[4096]; new Random(7).NextBytes(bytes); Update update=Info("2026.10.23.7",bytes);
        var handler=new Handler{Body=Encoding.UTF8.GetBytes(SignedUpdate(update))};
        using(var client=Core.Client(handler)) { var actual=await Core.Check(client,config,"2026.10.23.6",CancellationToken.None); OK(actual.Version==update.Version && handler.Url=="https://github.com/ADELIEBEAR/SwarmAssault2/releases/latest/download/swarm-update.json","production manifest URL + signature check"); }
        handler=new Handler{Body=bytes}; string target=Path.Combine(work,"download.exe");
        using(var client=Core.Client(handler)) await Core.Download(client,config,update,target,n=>{},CancellationToken.None);
        OK(File.ReadAllBytes(target).SequenceEqual(bytes) && handler.Url.Contains("/releases/download/v2026.10.23.7/"),"verified immutable release download");
        bytes[0]^=1; handler=new Handler{Body=bytes};
        using(var client=Core.Client(handler)) { bool failed=false; try { await Core.Download(client,config,update,target,n=>{},CancellationToken.None); } catch(InvalidDataException) {failed=true;} OK(failed && Core.HashFile(target)==update.Sha256,"corrupt download keeps previous cached installer"); }
        handler=new Handler{Body=new byte[10]};
        using(var client=Core.Client(handler)) { bool failed=false; try {await Core.Download(client,config,update,target,n=>{},CancellationToken.None);} catch(InvalidDataException){failed=true;} OK(failed,"truncated download rejected"); }
        OK(!Directory.GetFiles(work,"*.partial-*").Any(),"partial download cleanup");
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            work=Path.GetFullPath(args[0]);stub=Path.GetFullPath(args[1]);assembly=Path.GetFullPath(args[2]);Directory.CreateDirectory(work);
            using(var rsa=new RSACryptoServiceProvider(2048)) {rsa.PersistKeyInCsp=false; privateXml=rsa.ToXmlString(true);config=new Config{Repository="ADELIEBEAR/SwarmAssault2",PublicKeyXml=rsa.ToXmlString(false)};}
            string good=SignedUpdate(Info("2026.10.23.7",new byte[2048]));
            OK(Core.VerifyUpdate(good,config,"2026.10.23.6").Version=="2026.10.23.7","valid RSA-SHA256 manifest");
            OK(Core.VerifyUpdate(good,config,"2026.10.23.7")==null,"same version ignored");
            OK(Core.VerifyUpdate(good,config,"2026.10.23.8")==null,"downgrade ignored");
            var envelope=Core.Json.Deserialize<Signed>(good); envelope.Signature=Convert.ToBase64String(new byte[256]);
            Throws(()=>Core.VerifyUpdate(Core.Json.Serialize(envelope),config,"2026.10.23.6"),"bad signature rejected");
            var wrong=Info("2026.10.23.7",new byte[2048]);wrong.Repository="other/repo";
            Throws(()=>Core.VerifyUpdate(SignedUpdate(wrong),config,"2026.10.23.6"),"wrong repository rejected");
            wrong=Info("2026.10.23.7",new byte[2048]);wrong.FileName="../../outside.exe";
            Throws(()=>Core.VerifyUpdate(SignedUpdate(wrong),config,"2026.10.23.6"),"untrusted asset path rejected");
            Throws(()=>Core.Inside(work,"../escape"),"ZIP traversal rejected");
            Throws(()=>Core.Inside(work,"C:/escape"),"absolute ZIP path rejected");
            Throws(()=>Core.Root(work,false),"unsafe install root rejected");
            Network().GetAwaiter().GetResult();
            string root=Path.Combine(work,"설치 경로 space","SwarmAssaultII"),profile=Path.Combine(work,"저장 경로 space","Profile");
            string v1=Fixture("2026.10.23.6",null,false);
            Throws(()=>Core.Install(v1,root,Path.Combine(root,"Profile"),false,false,(s,n)=>{}),"profile inside install directory rejected to protect saves during uninstall");
            Core.Install(v1,root,profile,false,false,(s,n)=>{});
            OK(Core.Read<Active>(Path.Combine(root,"active-version.json")).Version=="2026.10.23.6","fresh install committed");
            Environment.SetEnvironmentVariable("SII_STUB_OUTPUT",Path.Combine(work,"arguments.txt"));
            using(var game=Core.Launch(root)) {if(!game.WaitForExit(15000)) throw new Exception("Stub timeout");OK(game.ExitCode==0,"game process launch");}
            var actualArgs=File.ReadAllLines(Path.Combine(work,"arguments.txt"));
            OK(actualArgs[0]=="Engine.SupportDir="+profile && actualArgs.Contains("Graphics.MaxFramerate=240"),"Unicode/spaces profile path + real argv + 240 FPS cap");
            string tricky="quote \" in path with slash\\";
            using(var p=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(stub,Core.Args(tricky,"second")){UseShellExecute=false,WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden})) p.WaitForExit();
            OK(File.ReadAllLines(Path.Combine(work,"arguments.txt"))[0]==tricky,"Windows argv quoting including trailing slash and quote");
            var preserved=new Dictionary<string,string>{{"settings.yaml","Graphics:\n\tUIScale: 1.5\n"},{"campaign-progress.json","{\"level\":42}"},{"maps/sa/my-map.txt","custom map"}};
            foreach(var kv in preserved){string p=Path.Combine(profile,kv.Key);Directory.CreateDirectory(Path.GetDirectoryName(p));File.WriteAllText(p,kv.Value);}
            string v2=Fixture("2026.10.23.7",null,false);
            Throws(()=>Core.Install(Fixture("2026.10.23.7",null,true),root,null,false,false,(s,n)=>{}),"bad file hash prevents activation");
            OK(Core.Read<Active>(Path.Combine(root,"active-version.json")).Version=="2026.10.23.6","old version survives failed update");
            Throws(()=>Core.Install(Fixture("2026.10.23.7","../escaped.txt",false),root,null,false,false,(s,n)=>{}),"malicious ZIP entry rejected before activation");
            byte[] tampered=File.ReadAllBytes(v2);tampered[File.ReadAllBytes(assembly).Length+52]^=1;string bad=Path.Combine(work,"tampered.exe");File.WriteAllBytes(bad,tampered);
            Throws(()=>Core.Install(bad,root,null,false,false,(s,n)=>{}),"tampered ZIP overlay rejected");
            string fallbackOutput=Path.Combine(work,"fallback-arguments.txt");
            Environment.SetEnvironmentVariable("SII_STUB_OUTPUT",fallbackOutput);
            using(var updater=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(bad,Core.Args("--silent","--install-root",root,"--no-register","--no-shortcuts","--launch")){UseShellExecute=false,WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden}))
            { if(!updater.WaitForExit(15000)) throw new Exception("Failed updater timeout");OK(updater.ExitCode==1,"failed installer reports failure"); }
            for(int i=0;i<100 && !File.Exists(fallbackOutput);i++) Thread.Sleep(50);
            OK(File.Exists(fallbackOutput) && File.ReadAllLines(fallbackOutput).FirstOrDefault()=="Engine.SupportDir="+profile && Core.Read<Active>(Path.Combine(root,"active-version.json")).Version=="2026.10.23.6","failed installer launches unchanged installed game without another update check");
            Core.Install(v2,root,null,false,false,(s,n)=>{});
            OK(Core.Read<Active>(Path.Combine(root,"active-version.json")).Version=="2026.10.23.7" && Core.Read<Active>(Path.Combine(root,"previous-version.json")).Version=="2026.10.23.6","update switches atomically and retains previous version");
            OK(preserved.All(kv=>File.ReadAllText(Path.Combine(profile,kv.Key))==kv.Value),"campaign/settings/custom map preserved across update");
            OK(Core.Read<Config>(Path.Combine(root,"distribution.json")).DataRoot==profile,"profile location preserved across update");
            Throws(()=>Core.Install(v1,root,null,false,false,(s,n)=>{}),"installer downgrade rejected");
            Core.Install(v2,root,null,false,false,(s,n)=>{});
            OK(Core.Read<Active>(Path.Combine(root,"active-version.json")).Engine.Contains("-repair-"),"same-version repair uses immutable slot");
            using(var form=new ShellForm(true,assembly,Core.DefaultRoot)) { var handle=form.Handle; foreach(System.Windows.Forms.Control child in form.Controls){var childHandle=child.Handle;} form.PerformLayout(); using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(work,"installer-preview.png"));}}
            Core.Uninstall(root,false);
            OK(!Directory.Exists(root) && preserved.All(kv=>File.Exists(Path.Combine(profile,kv.Key))),"uninstall preserves external profile");
            File.WriteAllText(Path.Combine(work,"test-result.txt"),passed+" passed\n");Console.WriteLine("ALL "+passed+" PASSED");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
