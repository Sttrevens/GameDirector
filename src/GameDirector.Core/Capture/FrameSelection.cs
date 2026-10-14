using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
namespace GameDirector.Core.Capture
{
    /// <summary>Usage failures are distinct from take admission and engine protocol failures.</summary>
    public sealed class FrameSelectionException : ArgumentException
    {
        public FrameSelectionException(string message):base("--frames: "+message){}
    }
    public static class FrameSelection
    {
        /// <summary>Syntax only, no expansion or I/O; timeline range admission happens later.</summary>
        public static void CheckSyntax(string spec)=>Ranges(spec);
        public static int[] Parse(string spec,int frameCount)
        {
            var ranges=Ranges(spec);if(frameCount<=0)throw new ArgumentException("timeline has no frames");
            var frames=new SortedSet<int>();
            foreach(var range in ranges) {
                if(range.End>=frameCount)throw new FrameSelectionException("item '"+range.Item+"' contains index "+range.End+"; last frame index is "+(frameCount-1));
                for(int i=range.Start;;i++){frames.Add(i);if(i==range.End)break;}
            }
            var result=new int[frames.Count];frames.CopyTo(result);return result;
        }
        private static List<(int Start,int End,string Item)> Ranges(string spec)
        {
            var result=new List<(int,int,string)>();
            foreach(var raw in (spec??"").Split(',')) {
                var item=raw.Trim();if(item.StartsWith("-",StringComparison.Ordinal))throw new FrameSelectionException("item '"+item+"' must be a nonnegative frame index");
                var pair=item.Split('-');if(pair.Length>2)throw new FrameSelectionException("item '"+item+"' is not a number or inclusive A-B range");
                var start=Index(pair[0],item);var end=pair.Length==1?start:Index(pair[1],item);
                if(start>end)throw new FrameSelectionException("item '"+item+"' has descending range (A must be <= B)");
                result.Add((start,end,item));
            }
            return result;
        }
        private static int Index(string text,string item)
        {
            text=text.Trim();if(text.Length==0||text.Any(c=>c<'0'||c>'9'))throw new FrameSelectionException("item '"+item+"' is not a number");
            if(!int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var value))throw new FrameSelectionException("item '"+item+"' is out of int range");
            return value;
        }
    }
}
