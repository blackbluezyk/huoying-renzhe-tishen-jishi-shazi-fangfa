using System;
using System.Drawing;
using System.IO;
using System.Text;
using EnergyBeanMonitor;
public static class OfflineUnitTests
{
    static int failures;
    static void Check(string name,bool pass){Console.WriteLine((pass?"PASS ":"FAIL ")+name);if(!pass)failures++;}
    static int Stable(Detector d,int n,double t){d.Observe(n,t);d.Observe(n,t+.05);d.Observe(n,t+.1);return d.Observe(n,t+.2);}
    public static int Main(string[] args)
    {
        Console.OutputEncoding=new UTF8Encoding(false);
        Detector left=new Detector{MaxBeans=6},right=new Detector();
        Stable(left,6,0);Stable(right,4,0);
        Check("left_6_to_4_creates_two",Stable(left,4,1)==2&&left.Timers.Count==2);
        Check("right_4_to_3_creates_one",Stable(right,3,1)==1&&right.Timers.Count==1);
        Check("sides_independent",left.Events==2&&right.Events==1);
        Check("default_exact_15s_deadline",Math.Abs(left.Timers[0].Ends-16.2)<1e-8&&left.Timers[0].Remaining(16.2)<1e-8);
        Stable(left,6,2);Check("refill_no_new_timer",left.Events==2);
        left.Observe(null,3);Stable(left,5,3.1);Check("short_unknown_then_drop",left.Events==3);
        left.ResetDetection();Stable(left,3,4);Check("resume_no_false_consumption",left.Events==3);
        Detector configurable=new Detector{Seconds=12.5};Stable(configurable,4,0);Stable(configurable,3,1);
        Check("configurable_seconds_monotonic",Math.Abs(configurable.Timers[0].Remaining(1.2)-12.5)<1e-8);
        Color[] colors={Color.FromArgb(250,70,35),Color.FromArgb(172,236,251),Color.FromArgb(250,180,40),Color.FromArgb(23,47,96),Color.FromArgb(96,31,23),Color.FromArgb(30,250,30),Color.Black};
        bool[] valid={true,true,true,true,true,false,false},lit={true,true,true,false,false,false,false};
        for(int i=0;i<colors.Length;i++)using(Bitmap b=new Bitmap(25,25))
        {using(Graphics g=Graphics.FromImage(b))g.Clear(colors[i]);Sample s=Vision.Read(b,new Point(12,12),4,160);Check("color_case_"+i,s.Valid==valid[i]&&(!valid[i]||s.Lit==lit[i]));}
        string dir=args[0];Directory.CreateDirectory(dir);string config=Path.Combine(dir,"typed-test.xml");
        PointF[] points=new PointF[6];for(int i=0;i<6;i++)points[i]=new PointF(.1f+i*.05f,.2f);
        SavedSettings settings=new SavedSettings{TargetTitle="MuMu fixture",LeftSlots=6,LeftPoints=points,LeftWidth=1920,Mode=2};
        SettingsStore.Save(config,settings);settings.Threshold=170;SettingsStore.Save(config,settings);
        SavedSettings loaded=SettingsStore.Load(config);
        Check("typed_xml_roundtrip_and_atomic_update",loaded.LeftSlots==6&&loaded.LeftPoints.Length==6&&loaded.Threshold==170&&loaded.Seconds==15);
        string xxe=Path.Combine(dir,"invalid-entity.xml");
        File.WriteAllText(xxe,"<!DOCTYPE SavedSettings [<!ENTITY e SYSTEM 'file:///nonexistent-offline-test'>]><SavedSettings><TargetTitle>&e;</TargetTitle></SavedSettings>");
        bool rejected=false;try{SettingsStore.Load(xxe);}catch{rejected=true;}Check("dtd_external_entities_rejected",rejected);
        settings.LeftSlots=5;rejected=false;try{SettingsStore.Save(config,settings);}catch(InvalidDataException){rejected=true;}Check("invalid_slot_count_rejected",rejected);
        File.WriteAllText(Path.Combine(dir,"too-large.xml"),new string('x',70000));rejected=false;try{SettingsStore.Load(Path.Combine(dir,"too-large.xml"));}catch(InvalidDataException){rejected=true;}Check("oversized_config_rejected",rejected);
        Console.WriteLine("EXIT="+(failures==0?0:1));return failures==0?0:1;
    }
}
