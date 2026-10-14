using GameDirector.Workbench;
using Microsoft.AspNetCore.Http;

namespace GameDirector.Decisions.Tests;

public class WorkbenchAuthTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public void CodesExpireAtSixtySecondsAndConsumedCodesNeverReturn()
    {
        var clock = new Clock(); var auth = new WorkbenchAuth(clock);
        var code = auth.MintCode(); clock.Now += TimeSpan.FromSeconds(59);
        var session = auth.Exchange(code);
        Assert.Throws<UnauthorizedFault>(() => auth.Exchange(code));
        var expired = auth.MintCode(); clock.Now += TimeSpan.FromSeconds(60);
        Assert.Throws<UnauthorizedFault>(() => auth.Exchange(expired));
        var request = new DefaultHttpContext().Request; request.Path = "/api/state";
        request.Headers.Cookie = "gd_session=" + session;
        Assert.True(auth.Allows(request)); // session lasts for this instance, not 60 s
        Assert.False(new WorkbenchAuth(clock).Allows(request));
    }
    [Fact]
    public async Task ConcurrentCodeExchangeAdmitsExactlyOneBrowser()
    {
        var auth = new WorkbenchAuth(); var code = auth.MintCode();
        var exchanges = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => {
            try { auth.Exchange(code); return true; } catch (UnauthorizedFault) { return false; }
        })));
        Assert.Single(exchanges.Where(success => success));
    }
}
