using System;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.TestTools;
using GameDirector.Core.Dsl;
using NUnit.Framework;
namespace GameDirector.Unity.Tests {
public class BridgeErrorTests {
    [Test] public void OnlyDeliberateBridgeErrorsAreClassifiedAsCallerOrTransientFailures() {
        var type=typeof(DirectorRuntime).Assembly.GetType("GameDirector.Unity.BridgeRequestException");Assert.IsNotNull(type);
        var map=type.GetMethod("Classify",BindingFlags.Public|BindingFlags.Static);Assert.IsNotNull(map);
        var protocol=typeof(FrameClock).Assembly.GetType("GameDirector.Core.Dsl.BridgeErrors");Assert.IsNotNull(protocol);
        foreach(var row in new[]{("InvalidRequest",400,"invalid_request"),("StateConflict",409,"state_conflict"),("Busy",503,"busy"),("Timeout",504,"timeout"),("Internal",500,"internal")}) {
            var definition=protocol.GetField(row.Item1).GetValue(null);
            var error=(Exception)Activator.CreateInstance(type,definition,"fixture diagnosis");
            var classified=map.Invoke(null,new object[]{error});
            Assert.AreEqual(row.Item2,classified.GetType().GetProperty("Status").GetValue(classified));
            Assert.AreEqual(row.Item3,classified.GetType().GetProperty("Code").GetValue(classified));
            Assert.AreEqual("fixture diagnosis",error.Message);
        }
        foreach(var error in new Exception[]{new NullReferenceException("adapter bug"),new ArgumentException("not deliberately classified"),new InvalidOperationException("engine failed")}) {
            var classified=map.Invoke(null,new object[]{error});
            Assert.AreEqual(500,classified.GetType().GetProperty("Status").GetValue(classified));
            Assert.AreEqual("internal",classified.GetType().GetProperty("Code").GetValue(classified));
        }
    }
    [Test] public void TheActualQueueCatchKeepsUnknownFailuresInternal() {
        var go=new GameObject("queued-fault");go.SetActive(false); // never start an HTTP listener
        try {
            var server=go.AddComponent<DirectorBridgeServer>();var type=typeof(DirectorBridgeServer);
            var jobType=type.GetNestedType("Job",BindingFlags.NonPublic);var job=Activator.CreateInstance(jobType,true);
            jobType.GetField("Work").SetValue(job,new Func<byte[]>(()=>{throw new NullReferenceException("queued adapter bug");}));
            var queue=type.GetField("_jobs",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(server);
            queue.GetType().GetMethod("Enqueue").Invoke(queue,new[]{job});
            type.GetField("_queued",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(server,1);
            LogAssert.Expect(LogType.Exception,new Regex("NullReferenceException: queued adapter bug"));
            type.GetMethod("Update",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(server,null);
            Assert.AreEqual(500,jobType.GetField("Status").GetValue(job));
            Assert.AreEqual("internal",jobType.GetField("Code").GetValue(job));Assert.AreEqual(2,jobType.GetField("State").GetValue(job));
            var body=Newtonsoft.Json.Linq.JObject.Parse((string)jobType.GetField("Error").GetValue(job));
            Assert.AreEqual("queued adapter bug",(string)body["error"]);Assert.AreEqual("internal",(string)body["code"]);
        } finally {UnityEngine.Object.DestroyImmediate(go);}
    }
    // Drive the production route through the production queue/catch. Replacing
    // Read<T> with raw Deserialize, or removing the race guard, must fail.
    [TestCase("/take/start")] [TestCase("/take/stop")]
    public void MalformedTakeRouteBodiesRemainInvalidRequests(string route) {
        QueuedRouteAnswer("POST",route,"{broken",false,400,"invalid_request");
    }
    [Test] public void QueuedUnknownRouteIsNotFound() {
        QueuedRouteAnswer("POST","/no-such-route","{}",false,404,"not_found");
    }
    [Test] public void StopDrainsQueuedWorkWithABusyCodeBearingAnswer() {
        var go=new GameObject("stopping-queue");go.SetActive(false);
        try {
            var server=go.AddComponent<DirectorBridgeServer>();var type=typeof(DirectorBridgeServer);
            var jobType=type.GetNestedType("Job",BindingFlags.NonPublic);var job=Activator.CreateInstance(jobType,true);
            var executed=false;jobType.GetField("Work").SetValue(job,new Func<byte[]>(()=>{executed=true;return null;}));
            var queue=type.GetField("_jobs",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(server);
            queue.GetType().GetMethod("Enqueue").Invoke(queue,new[]{job});
            type.GetField("_queued",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(server,1);
            type.GetMethod("OnDisable",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(server,null);
            Assert.AreEqual(503,jobType.GetField("Status").GetValue(job));
            Assert.AreEqual("busy",jobType.GetField("Code").GetValue(job));
            var body=Newtonsoft.Json.Linq.JObject.Parse((string)jobType.GetField("Error").GetValue(job));
            Assert.AreEqual("busy",(string)body["code"]);Assert.AreEqual("bridge stopped",(string)body["error"]);
            Assert.AreEqual(-1,jobType.GetField("State").GetValue(job));Assert.IsFalse(executed);
            var done=(System.Threading.Tasks.TaskCompletionSource<bool>)jobType.GetField("Done").GetValue(job);
            Assert.IsTrue(done.Task.IsCompleted);Assert.IsFalse(done.Task.Result);
            Assert.AreEqual(0,type.GetField("_queued",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(server));
        } finally {UnityEngine.Object.DestroyImmediate(go);}
    }
    [Test] public void QueuedNonTakeRouteCannotSlipPastTheCaptureRaceGuard() {
        QueuedRouteAnswer("GET","/status",null,true,503,"busy");
    }
    private static void QueuedRouteAnswer(string method,string path,string json,bool capturing,int status,string code) {
        var go=new GameObject("queued-route");go.SetActive(false);
        try {
            var runtime=go.AddComponent<DirectorRuntime>();
            typeof(DirectorRuntime).GetField("_capturing",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(runtime,capturing);
            var server=go.AddComponent<DirectorBridgeServer>();var type=typeof(DirectorBridgeServer);
            type.GetField("_runtime",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(server,runtime);
            var route=type.GetMethod("RouteOnMainThread",BindingFlags.NonPublic|BindingFlags.Instance);
            var jobType=type.GetNestedType("Job",BindingFlags.NonPublic);var job=Activator.CreateInstance(jobType,true);
            jobType.GetField("Work").SetValue(job,new Func<byte[]>(()=> {
                try {return (byte[])route.Invoke(server,new object[]{method,path,json==null?null:System.Text.Encoding.UTF8.GetBytes(json)});}
                catch(TargetInvocationException ex) {throw ex.InnerException;}
            }));
            var queue=type.GetField("_jobs",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(server);
            queue.GetType().GetMethod("Enqueue").Invoke(queue,new[]{job});
            type.GetField("_queued",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(server,1);
            type.GetMethod("Update",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(server,null);
            Assert.AreEqual(status,jobType.GetField("Status").GetValue(job));
            Assert.AreEqual(code,jobType.GetField("Code").GetValue(job));
            var body=Newtonsoft.Json.Linq.JObject.Parse((string)jobType.GetField("Error").GetValue(job));
            Assert.AreEqual(code,(string)body["code"]);Assert.IsNotEmpty((string)body["error"]);
        } finally {UnityEngine.Object.DestroyImmediate(go);}
    }
    [Test] public void MalformedWireDocumentsAreInvalidRequests() {
        var read=typeof(BridgeRequestException).GetMethod("Read",BindingFlags.NonPublic|BindingFlags.Static).MakeGenericMethod(typeof(GameDirector.Core.Capture.TakeRequest));
        foreach(var json in new[]{"{broken","null","{\"width\":1e10}","[]"}) {
            var reflection=Assert.Throws<TargetInvocationException>(()=>read.Invoke(null,new object[]{json}));
            Assert.IsInstanceOf<BridgeRequestException>(reflection.InnerException);
            Assert.AreSame(BridgeErrors.InvalidRequest,((BridgeRequestException)reflection.InnerException).Error);
        }
    }
    [Test] public void TheJobPathTurnsAnUnclassifiedFailureIntoAnInternalCodeBearingAnswer() {
        var server=typeof(DirectorRuntime).Assembly.GetType("GameDirector.Unity.DirectorBridgeServer");Assert.IsNotNull(server);
        var diagnose=server.GetMethod("Diagnose",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static);Assert.IsNotNull(diagnose);
        var answer=diagnose.Invoke(null,new object[]{new NullReferenceException("adapter bug")});
        Assert.AreEqual(500,answer.GetType().GetField("Status").GetValue(answer));
        Assert.AreEqual("internal",answer.GetType().GetField("Code").GetValue(answer));
        var body=Newtonsoft.Json.Linq.JObject.Parse((string)answer.GetType().GetField("Body").GetValue(answer));
        Assert.AreEqual("adapter bug",(string)body["error"]);Assert.AreEqual("internal",(string)body["code"]);
        var classified=diagnose.Invoke(null,new object[]{new BridgeRequestException(BridgeErrors.StateConflict,"frame index mismatch")});
        Assert.AreEqual(409,classified.GetType().GetField("Status").GetValue(classified));
        Assert.AreEqual("state_conflict",classified.GetType().GetField("Code").GetValue(classified));
    }
}
}
