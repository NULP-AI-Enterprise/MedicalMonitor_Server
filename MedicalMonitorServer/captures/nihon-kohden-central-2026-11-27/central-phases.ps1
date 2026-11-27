# Which of the central station's requests makes the BSM-3562 thin out its waveform stream?
# Subscribes to the five waveforms the server asks for, as the server does, and then polls the
# monitor in phases, recording every datagram:
#   phase 0  stream only
#   phase 1  the limits request alone (0301)
#   phase 2  the central poll without 0206 "monitor this bed"
#   phase 3  the whole central poll, as deployed on 2026-11-27
#   phase 4  stream only again: does it recover by itself?
# The installed server must be stopped while this runs: it holds UDP 8300, 8301 and 55405.
param(
    [string]$LocalIp   = '10.200.200.77',
    [string]$MonitorIp = '10.10.46.48',
    [string]$Phases = '6,10,10,10,10',
    [Parameter(Mandatory = $true)][string]$Out
)
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

public static class CentralPhases {
    static string Hex(byte[] b,int n){var h=new StringBuilder(n*3);for(int i=0;i<n;i++)h.Append(b[i].ToString("X2")).Append(' ');return h.ToString();}
    static byte[] Msg(byte kindLow,ushort seq,byte tHi,byte tLo,byte[] id,byte[] ip,byte[] payload){
        int len=32+payload.Length;var b=new byte[len];
        b[1]=kindLow;b[2]=(byte)(seq>>8);b[3]=(byte)seq;b[4]=(byte)(len>>8);b[5]=(byte)len;b[7]=1;b[9]=1;
        b[12]=tHi;b[13]=tLo;b[16]=(byte)((len-12)>>8);b[17]=(byte)(len-12);
        id.CopyTo(b,24);ip.CopyTo(b,28);payload.CopyTo(b,32);return b;}

    public static void Run(int[] phaseSeconds,string localIp,string monitorIp,string path){
        var local=IPAddress.Parse(localIp);var monitor=IPAddress.Parse(monitorIp);
        var a=local.GetAddressBytes();var m=monitor.GetAddressBytes();
        var monId=new byte[]{m[1],m[2],m[3],0x20};

        var beacon=new byte[64];
        new byte[]{0x04,0xFF,0,0,0,0x40,0,1,0,1,0,0,0x02,0x01,0,0,0,0x34}.CopyTo(beacon,0);
        beacon[24]=a[1];beacon[25]=a[2];beacon[26]=a[3];a.CopyTo(beacon,28);beacon[33]=0x01;
        Encoding.ASCII.GetBytes("STATION").CopyTo(beacon,36);
        a.CopyTo(beacon,48);beacon[52]=a[1];beacon[53]=a[2];beacon[54]=a[3];beacon[55]=0x00;
        beacon[60]=0x01;beacon[61]=0x86;beacon[62]=0x01;

        // ECG II, arterial pressure, pleth, central venous pressure, respiration: what the server asks for.
        ushort[] waves={0x0101,0x2C00,0x0700,0x3500,0x0500};
        var subscribe=new byte[8+4*waves.Length+16];
        subscribe[0]=4;subscribe[6]=(byte)waves.Length;
        for(int i=0;i<waves.Length;i++){subscribe[8+4*i]=8;subscribe[10+4*i]=(byte)(waves[i]>>8);subscribe[11+4*i]=(byte)waves[i];}
        new byte[]{1,0,0,0,0x55,0x55,0x55,0x7F,1,0,0,0,0x55,0x55,0x55,0x57}.CopyTo(subscribe,8+4*waves.Length);
        var bare=new byte[]{0x06,0xFF,0,0,0,0x0C,0,1,0,1,0,0};

        // The seven requests of the central's poll: type high, type low, selector byte.
        byte[][] all = new byte[][]{
            new byte[]{0x02,0x06,0x07}, new byte[]{0x03,0x08,0x09}, new byte[]{0x03,0x04,0x05},
            new byte[]{0x03,0x01,0x02}, new byte[]{0x04,0x04,0x05}, new byte[]{0x04,0x01,0x02},
            new byte[]{0x03,0x20,0x21}
        };
        byte[][] limitsOnly = new byte[][]{ all[3] };
        byte[][] without0206 = new byte[][]{ all[1], all[2], all[3], all[4], all[5], all[6] };
        byte[][] none = new byte[0][];
        byte[][][] polls = new byte[][][]{ none, limitsOnly, without0206, all, none };
        string[] names = { "0 stream only", "1 limits request 0301 alone", "2 the central poll without 0206", "3 the whole central poll", "4 stream only again" };

        var control=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);control.EnableBroadcast=true;control.Bind(new IPEndPoint(local,55405));
        var in8300=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);in8300.EnableBroadcast=true;in8300.Bind(new IPEndPoint(IPAddress.Any,8300));
        var in8301=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);in8301.Bind(new IPEndPoint(IPAddress.Any,8301));
        var socks=new[]{control,in8300,in8301};
        var bcast=new[]{new IPEndPoint(IPAddress.Parse("10.255.255.255"),8300),new IPEndPoint(IPAddress.Broadcast,8300)};
        var monCtl=new IPEndPoint(monitor,8300);

        var buf=new byte[65536];ushort seq=0x0900;
        var start=DateTime.UtcNow;
        var phaseEnd=new DateTime[phaseSeconds.Length];
        var t=start;for(int i=0;i<phaseSeconds.Length;i++){t=t.AddSeconds(phaseSeconds[i]);phaseEnd[i]=t;}
        var end=t;
        var nextBeacon=start;var nextSub=start;var nextPoll=start;var lastStream=DateTime.MinValue;
        int phase=-1;

        using(var raw=new StreamWriter(path,false){AutoFlush=true}){
            raw.WriteLine("# central-phases probe: five waveforms, then the central's requests in phases; see central-phases.ps1");
            while(DateTime.UtcNow<end){
                var now=DateTime.UtcNow;
                int current=0;while(current<phaseEnd.Length-1&&now>=phaseEnd[current])current++;
                if(current!=phase){
                    phase=current;nextPoll=now;
                    raw.WriteLine("# "+DateTime.Now.ToString("HH:mm:ss.fff")+" phase "+names[phase]);
                }
                if(now>=nextBeacon){foreach(var target in bcast){try{control.SendTo(beacon,target);}catch{}}nextBeacon=now.AddSeconds(3);}
                if(now>=nextSub&&(now-lastStream).TotalSeconds>2){
                    var pkt=Msg(0,++seq,0x02,0x22,monId,a,subscribe);control.SendTo(pkt,monCtl);nextSub=now.AddSeconds(1);
                    raw.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff")+" REQ 0222 "+Hex(pkt,pkt.Length));
                }
                if(polls[phase].Length>0&&now>=nextPoll){
                    foreach(var r in polls[phase]){
                        var pkt=Msg((byte)(r[0]==0x03?1:0),++seq,r[0],r[1],monId,a,new byte[]{r[2],0,0,0});
                        control.SendTo(pkt,monCtl);
                        raw.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff")+" REQ "+r[0].ToString("X2")+r[1].ToString("X2")+" "+Hex(pkt,pkt.Length));
                    }
                    nextPoll=now.AddSeconds(2);
                }
                foreach(var s in socks){
                    try{while(s.Available>0){
                        EndPoint ep=new IPEndPoint(IPAddress.Any,0);
                        int n=s.ReceiveFrom(buf,ref ep);var from=(IPEndPoint)ep;
                        if(!from.Address.Equals(monitor))continue;
                        if(n==64&&buf[0]==0x04)continue;
                        int port=((IPEndPoint)s.LocalEndPoint).Port;
                        if(n>=2&&buf[0]==0x02&&(buf[1]==0x0B||buf[1]==0x12))lastStream=DateTime.UtcNow;
                        if(buf[0]==0x05)control.SendTo(bare,monCtl);
                        raw.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff")+" IN "+from.Port+">"+port+" "+n+" "+Hex(buf,n));
                    }}catch{}
                }
                System.Threading.Thread.Sleep(5);
            }
            // No goodbye: the server takes the stream over within a second.
        }
        foreach(var s in socks)s.Close();
    }
}
'@
$seconds = [int[]]($Phases -split ',')
"central-phases probe -> $Out ($(($seconds | Measure-Object -Sum).Sum) s)"
[CentralPhases]::Run($seconds, $LocalIp, $MonitorIp, $Out)
"done"
