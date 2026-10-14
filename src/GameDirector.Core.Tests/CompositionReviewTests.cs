using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Core.Tests;
// These rows kill tolerance-sign, severity, single-subject and near-plane mistakes.
public class CompositionReviewTests
{
    private static readonly double[] From={0,1,-4},Aim={0,1,0};
    private static double[] Point(double x,double y,double aspect=1)=>new[]{(x-.5)*8*Math.Tan(25*Math.PI/180)*aspect,1+(y-.5)*8*Math.Tan(25*Math.PI/180),0};
    private static CompositionRole Rect(double left,double right,double crown=.8,string kind="subject",double aspect=1)
    {
        var p=CameraReference.Bounds(Point(left,.2,aspect),Point(right,crown,aspect),From,Aim,50,aspect);
        return new(){Role=kind=="with"?"partner":"hero",Kind=kind,State=p.State,Rect=p.Rect,Crown=p.Crown};
    }
    private static TimelineAsset Timeline(bool pair=false,Dictionary<string,float>? parameters=null,string frame=ShotVocabulary.OmittedFrameId)=>new(){Id="review",Cues=new(){new(){T=0,Type=CueTypes.CameraShot,Shot=new(){Subject="hero",With=pair?new(){"partner"}:null,DurationSeconds=2,Frame=frame,Params=parameters??new()}}}};
    private static CompositionVerdict Judge(CompositionRole role,bool pair=false,Dictionary<string,float>? parameters=null,SafeArea? safe=null,string frame=ShotVocabulary.OmittedFrameId)=>CompositionReview.Evaluate(new[]{new CompositionSample{Frame=0,Time=0,Composition=new(){Active=true,Subject="hero",Roles=new(){role}}}},Timeline(pair,parameters,frame),safe);
    [Theory] [InlineData(16d/9)] [InlineData(9d/16)] public void PairSideCutIsAnErrorAtBothAspects(double aspect)
    {
        var v=Judge(Rect(-.01,.7,kind:"with",aspect:aspect),pair:true);Assert.Equal(1,v.Errors);Assert.Contains(v.Findings,f=>f.Code=="side-cut"&&f.Severity=="error");
    }
    [Theory] [InlineData(-.004,0)] [InlineData(-.006,1)] public void SideToleranceIsOutwardNotInward(double left,int errors)
    {
        Assert.Equal(errors,Judge(Rect(left,.7,kind:"with"),pair:true).Errors);
    }
    [Fact] public void OrdinarySingleCloseupLateralCropIsSilentAndNeverAnError()
    {
        var v=Judge(Rect(-.01,.8));Assert.Equal(0,v.Errors);Assert.Empty(v.Findings);
    }
    [Theory] [InlineData("pan",10)] [InlineData("tilt",10)] [InlineData("roll",10)] [InlineData("pan",0)] [InlineData("tilt",0)] [InlineData("roll",0)] public void AuthoredAnglesWarnOnSingleSubjectSideCrop(string param,float value)
    {
        var v=Judge(Rect(-.01,.8),parameters:new(){{param,value}});Assert.Equal(0,v.Errors);Assert.Contains(v.Findings,f=>f.Code=="side-cut"&&f.Severity=="warning");
    }
    [Fact] public void LargeSingleSubjectLateralCropWarnsWithoutAngles()
    {
        var v=Judge(Rect(-.3,.7));Assert.Equal(0,v.Errors);Assert.Contains(v.Findings,f=>f.Code=="side-cut");
    }
    [Theory] [InlineData(1.004,false)] [InlineData(1.01,true)] public void CutCrownIsOnlyAWarning(double y,bool warns)
    {
        var v=Judge(Rect(.2,.8,y));Assert.Equal(0,v.Errors);Assert.Equal(warns,v.Findings.Any(f=>f.Code=="head-cut"));
    }
    [Fact] public void DefaultMediumAutoFrameWarnsFramedByCentreAndHasNoError()
    {
        var shot=new ShotSpec();var coverage=ShotVocabulary.Coverage(shot.Frame);var radius=CameraReference.AutoFrameRadius(2,coverage,50);
        var p=CameraReference.Bounds(new[]{-.5,0,-.5},new[]{.5,2,.5},new[]{0,1,-radius},Aim,50,9d/16);
        var v=Judge(new(){Role="hero",Kind="subject",State=p.State,Rect=p.Rect,Crown=p.Crown});Assert.Equal(0,v.Errors);
        Assert.Contains(v.Findings,f=>f.Code=="head-cut"&&f.Severity=="warning"&&f.Note=="framed-by-centre");
        var aimed=Judge(Rect(.2,.8,1.01),parameters:new(){{"targetHeight",1.7f}});Assert.Contains(aimed.Findings,f=>f.Code=="head-cut"&&f.Note==null);
    }
    [Theory] [InlineData("subject",1)] [InlineData("with",1)] [InlineData("lookAt",0)] public void BehindRoleLostSeverityDependsOnKind(string kind,int errors)
    {
        var p=CameraReference.Bounds(new[]{-.5,0,-9},new[]{.5,2,-8},From,Aim,50,1);
        var v=Judge(new(){Role="hero",Kind=kind,State=p.State,Rect=p.Rect,Crown=p.Crown},pair:true);Assert.Equal(errors,v.Errors);Assert.Contains(v.Findings,f=>f.Code=="lost");
    }
    [Fact] public void EntirelyOutsideIsLostEvenForSingleSubject()
    {
        var v=Judge(Rect(1.1,1.5));Assert.Contains(v.Findings,f=>f.Code=="lost"&&f.Severity=="error");
    }
    [Theory] [InlineData("absent")] [InlineData("empty")] public void AbsentOrEmptyWithRoleGivesNothing(string state)
    {
        Assert.Empty(Judge(new(){Role="partner",Kind="with",State=state},pair:true).Findings);
    }
    [Fact] public void BoundsStraddlingNearAreSideCutForProtectedRoles()
    {
        var p=CameraReference.Bounds(new[]{-.5,0,-4.02},new[]{.5,2,-3.9},From,Aim,50,1);Assert.Equal("straddles-near",p.State);Assert.Null(p.Rect);
        var role=new CompositionRole{Role="partner",Kind="with",State=p.State,Crown=p.Crown};Assert.Equal(1,Judge(role,pair:true).Errors);
        role.Kind="subject";Assert.Equal(0,Judge(role).Errors);Assert.Equal(1,Judge(role,pair:true).Errors);
    }
    [Fact] public void ErrorRoleWarnsInsteadOfInventingAGeometricVerdict()
    {
        var v=Judge(new(){Role="partner",Kind="with",State="error",Message="broken geometry"},pair:true);Assert.Equal(0,v.Errors);Assert.Contains(v.Findings,f=>f.Code=="bounds-error"&&f.Note=="broken geometry");
    }
    [Theory] [InlineData("full",0,false)] [InlineData("medium",.9f,false)] [InlineData("full",1.2f,true)] public void FramedByCentreUsesAuthoredCoverageOrFrameDefault(string frame,float coverage,bool centre)
    {
        var v=Judge(Rect(.2,.8,1.01),parameters:coverage==0?null:new(){{"coverage",coverage}},frame:frame);Assert.Equal(0,v.Errors);Assert.Equal(centre?"framed-by-centre":null,Assert.Single(v.Findings).Note);
    }
    [Theory] [InlineData(-.2,.4,true)] [InlineData(-.3,1.3,false)] [InlineData(-.5,1.5,false)] public void SingleSideFractionUsesActualRectWidth(double left,double right,bool warns)
    {
        var v=Judge(Rect(left,right));Assert.Equal(0,v.Errors);Assert.Equal(warns,v.Findings.Any(f=>f.Code=="side-cut"));
    }
    [Theory] [InlineData(1.01,1)] [InlineData(1.004,0)] [InlineData(1.006,1)] public void ProtectedRightEdgeUsesOutwardTolerance(double right,int errors)
    {
        Assert.Equal(errors,Judge(Rect(.3,right,kind:"with"),pair:true).Errors);
    }
    [Fact] public void EntirelyOutsideLeftIsLost()
    {
        Assert.Contains(Judge(Rect(-.5,-.1)).Findings,f=>f.Code=="lost"&&f.Severity=="error");
    }
    [Fact] public void CrownOutsideXWarnsEvenWhenRectIsInFrame()
    {
        var r=Rect(.2,.8);r.Crown=new[]{1.01,.8,4};var v=Judge(r);Assert.Equal(0,v.Errors);Assert.Contains(v.Findings,f=>f.Code=="head-cut"&&f.Severity=="warning");
    }
    [Fact] public void SamplesAtCutUseTheRightFrameAndWithListIncludingCueEpsilon()
    {
        var timeline=Timeline(pair:true,frame:"full");timeline.Cues.Add(new(){T=2,Type=CueTypes.CameraShot,Shot=new(){Subject="hero",Frame="medium",DurationSeconds=2}});
        var times=new[]{1.999,2-FrameClock.CueEpsilonSeconds/2,2,2+FrameClock.CueEpsilonSeconds/2};var samples=times.Select((t,i)=>new CompositionSample{Frame=i,Time=t,Composition=new(){Active=true,Subject="hero",Roles=new(){Rect(-.01,.7,1.01)}}});var v=CompositionReview.Evaluate(samples,timeline);
        Assert.Single(v.Findings.Where(f=>f.Severity=="error"&&f.Frame==0));Assert.DoesNotContain(v.Findings,f=>f.Severity=="error"&&f.Frame>0);
        Assert.Null(Assert.Single(v.Findings.Where(f=>f.Code=="head-cut"&&f.Frame==0)).Note);for(int i=1;i<times.Length;i++)Assert.Equal("framed-by-centre",Assert.Single(v.Findings.Where(f=>f.Code=="head-cut"&&f.Frame==i)).Note);
    }
    [Fact] public void NamedRoleVocabularyFeedsPublishedRowsAndLostEvaluation()
    {
        var spec=new RoleReviewSpec();Assert.Contains(RoleReviewVocabulary.With,spec.Kinds);Assert.Contains(RoleReviewVocabulary.StateBehind,spec.States);
        var v=Judge(new(){Role="partner",Kind=RoleReviewVocabulary.With,State=RoleReviewVocabulary.StateBehind});Assert.Equal(1,v.Errors);Assert.Contains(v.Findings,f=>f.Code==CompositionCodes.Lost);
    }
    [Fact] public void InjectedCompositionPolicyControlsGeometricThresholds()
    {
        var policy=new CompositionPolicy(.02,.02,.5);var samples=new[]{new CompositionSample{Composition=new(){Active=true,Roles=new(){Rect(-.01,.7,kind:"with")}}}};
        Assert.Equal(0,CompositionReview.Evaluate(samples,Timeline(pair:true),policy:policy).Errors);Assert.Equal(1,CompositionReview.Evaluate(samples,Timeline(pair:true)).Errors);
    }
    [Fact] public void InjectedPolicyControlsSingleSideFractionAndSafeSideMargin()
    {
        var samples=new[]{new CompositionSample{Composition=new(){Active=true,Roles=new(){Rect(-.3,1.3)}}}};
        Assert.Empty(CompositionReview.Evaluate(samples,Timeline()).Findings);Assert.Contains(CompositionReview.Evaluate(samples,Timeline(),policy:new CompositionPolicy(singleSideFraction:.1)).Findings,f=>f.Code==CompositionCodes.SideCut);
        samples[0].Composition.Roles=new(){Rect(.09,.8,kind:"with")};var area=SafeArea.Presets["tiktok"];
        Assert.Contains(CompositionReview.Evaluate(samples,Timeline(pair:true),area).Findings,f=>f.Code==CompositionCodes.Unsafe);Assert.Empty(CompositionReview.Evaluate(samples,Timeline(pair:true),area,new CompositionPolicy(safeSideTolerance:.03)).Findings);
    }
    [Fact] public void LegacyCentreHasWarningsOnly()
    {
        var v=CompositionReview.Evaluate(new[]{new CompositionSample{Composition=new(){Active=true,Subject="hero",Center=new[]{2d,.5,-1}}}},Timeline());
        Assert.Equal(0,v.Errors);Assert.Contains(v.Findings,f=>f.Severity=="warning"&&f.Code=="lost");
    }
    [Fact] public void TikTokBottomBandAndGroupSidesWarnOnly()
    {
        var v=Judge(Rect(.105,.8,.18),safe:SafeArea.Presets["tiktok"]);Assert.Equal(0,v.Errors);Assert.Contains(v.Findings,f=>f.Code=="unsafe");
        v=Judge(Rect(.09,.8,kind:"with"),pair:true,safe:SafeArea.Presets["tiktok"]);Assert.Equal(0,v.Errors);Assert.Contains(v.Findings,f=>f.Code=="unsafe");
    }
    [Fact] public void CroppedBottomAndInactiveReviewsAreNotErrors()
    {
        var r=Rect(.2,.8);r.Rect=new[]{.2,-.4,.8,.8};Assert.Empty(Judge(r).Findings);
        Assert.Empty(CompositionReview.Evaluate(new[]{new CompositionSample{Composition=new(){Active=false}}},Timeline()).Findings);
    }
}
