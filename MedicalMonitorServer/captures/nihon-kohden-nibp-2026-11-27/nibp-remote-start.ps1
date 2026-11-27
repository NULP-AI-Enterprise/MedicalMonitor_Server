# Live remote NIBP-start test against the real BSM-3562 (10.10.46.48).
# Claims the bed as controlling host exactly like the deployed server's subscribe
# (role byte payload[0]=0x04 + the same five wave codes + SubscribeTail), then sends ONE
# 0403 RemParamSet carrying param 0x0303 = 1 ("Start NIBP measurement", confirmed 3 ways).
# Scans EVERY received packet for 0x0908 (measuring) / 0x0900-02 (NIBP S/D/M), logs any 0403 the
# monitor pushes back, the subscribe-ack 0223 and 0207, and a periodic waveform heartbeat.
# Run ONLY while the installed server is stopped (it holds 8300/8301/55405). User-approved; cuff clear.
param(
    [string]$LocalIp    = '10.200.200.77',
    [string]$MonitorIp  = '10.10.46.48',
    [string]$StartHex   = '01 00 03 03 00 02 01 00',   # 0403 START record (STOP = ...00 00)
    [int]$Kind0         = 1,
    [int]$KindLow       = 0,
    [int]$SettleSec     = 10,
    [int]$DurationSec   = 55,
    [switch]$NoFire,
    [string]$Out        = "$PSScriptRoot\remote-start-02.raw.txt"
)
[uint16[]]$waves = 0x0101,0x2C00,0x0700,0x3500,0x0500
$payload = [byte[]]($StartHex -split '[ ,]+' | Where-Object { $_ -ne '' } | ForEach-Object { [byte]('0x'+$_) })

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

public static class NibpStart {
    static StreamWriter raw;
    static void Log(string s){ var line=DateTime.Now.ToString("HH:mm:ss.fff")+" "+s; raw.WriteLine(line); Console.WriteLine(line); }
    static string Hex(byte[] b,int n){var h=new StringBuilder(n*3);for(int i=0;i<n;i++)h.Append(b[i].ToString("X2")).Append(' ');return h.ToString();}

    static byte[] Msg(byte kind0, byte kindLow, ushort seq, byte tHi, byte tLo, byte[] id, byte[] ip, byte[] payload){
        int len=32+payload.Length; var b=new byte[len];
        b[0]=kind0; b[1]=kindLow;
        b[2]=(byte)(seq>>8); b[3]=(byte)seq;
        b[4]=(byte)(len>>8); b[5]=(byte)len;
        b[7]=1; b[9]=1;
        b[12]=tHi; b[13]=tLo;
        b[16]=(byte)((len-12)>>8); b[17]=(byte)(len-12);
        id.CopyTo(b,24); ip.CopyTo(b,28); payload.CopyTo(b,32);
        return b;
    }

    static byte[] Subscribe(ushort seq, byte[] id, byte[] ip, ushort[] waves){
        byte[] tail = {0x01,0x00,0x00,0x00,0x55,0x55,0x55,0x7F,0x01,0x00,0x00,0x00,0x55,0x55,0x55,0x57};
        var payload=new byte[8+4*waves.Length+tail.Length];
        payload[0]=0x04; payload[6]=(byte)waves.Length;
        for(int i=0;i<waves.Length;i++){ payload[8+4*i]=0x08; payload[10+4*i]=(byte)(waves[i]>>8); payload[11+4*i]=(byte)waves[i]; }
        tail.CopyTo(payload,8+4*waves.Length);
        return Msg(0,0,seq,0x02,0x22,id,ip,payload);
    }

    // scan the whole packet for a 2-byte code, return the 10-byte entry or "-"
    static string Find(byte[] b,int n,byte c0,byte c1){
        for(int i=20;i+10<=n;i++){ if(b[i]==c0&&b[i+1]==c1){ var s=new StringBuilder(); for(int k=0;k<10;k++) s.Append(b[i+k].ToString("X2")).Append(' '); return s.ToString().Trim(); } }
        return "-";
    }

    public static void Run(string localIp,string monitorIp,ushort[] waves,byte[] startPayload,byte kind0,byte kindLow,bool fire,int settleSec,int durationSec,string path){
        var local=IPAddress.Parse(localIp); var monitor=IPAddress.Parse(monitorIp);
        var a=local.GetAddressBytes(); var m=monitor.GetAddressBytes();
        var monId=new byte[]{m[1],m[2],m[3],0x20};

        var beacon=new byte[64];
        new byte[]{0x04,0xFF,0,0,0,0x40,0,1,0,1,0,0,0x02,0x01,0,0,0,0x34}.CopyTo(beacon,0);
        beacon[24]=a[1];beacon[25]=a[2];beacon[26]=a[3];a.CopyTo(beacon,28);beacon[33]=0x01;
        Encoding.ASCII.GetBytes("STATION").CopyTo(beacon,36);
        a.CopyTo(beacon,48);beacon[52]=a[1];beacon[53]=a[2];beacon[54]=a[3];beacon[55]=0x00;
        beacon[60]=0x01;beacon[61]=0x86;beacon[62]=0x01;

        var bare=new byte[]{0x06,0xFF,0,0,0,0x0C,0,1,0,1,0,0};

        var control=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);control.EnableBroadcast=true;control.Bind(new IPEndPoint(local,55405));
        var in8300=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);in8300.EnableBroadcast=true;in8300.Bind(new IPEndPoint(IPAddress.Any,8300));
        var in8301=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);in8301.Bind(new IPEndPoint(IPAddress.Any,8301));
        var socks=new[]{control,in8300,in8301};
        var bcast=new[]{new IPEndPoint(IPAddress.Parse("10.255.255.255"),8300),new IPEndPoint(IPAddress.Broadcast,8300)};
        var monCtl=new IPEndPoint(monitor,8300);

        var buf=new byte[65536]; ushort seq=0x0700;
        var startT=DateTime.UtcNow; var end=startT.AddSeconds(durationSec);
        var nextBeacon=startT; var nextSub=startT;
        bool fired=false; int waveCount=0; string last08="";

        raw=new StreamWriter(path,false){AutoFlush=true};
        Log("# claim host role 0x04 as STATION; fire="+fire+" kind0="+kind0+" payload=["+Hex(startPayload,startPayload.Length)+"] at T+"+settleSec+"s");
        while(DateTime.UtcNow<end){
            var now=DateTime.UtcNow;
            if(now>=nextBeacon){foreach(var t in bcast){try{control.SendTo(beacon,t);}catch{}}nextBeacon=now.AddSeconds(2.5);}
            if(now>=nextSub){control.SendTo(Subscribe(++seq,monId,a,waves),monCtl);nextSub=now.AddSeconds(1);}
            if(fire && !fired && (now-startT).TotalSeconds>=settleSec){
                var pkt=Msg(kind0,kindLow,++seq,0x04,0x03,monId,a,startPayload);
                control.SendTo(pkt,monCtl); fired=true;
                Log(">>> SENT 0403 START  "+Hex(pkt,pkt.Length));
            }
            foreach(var s in socks){
                int port=((IPEndPoint)s.LocalEndPoint).Port;
                try{while(s.Available>0){
                    EndPoint ep=new IPEndPoint(IPAddress.Any,0);
                    int n=s.ReceiveFrom(buf,ref ep);var from=(IPEndPoint)ep;
                    if(!from.Address.Equals(monitor))continue;
                    if(n==64&&buf[0]==0x04)continue;
                    if(buf[0]==0x05)control.SendTo(bare,monCtl);
                    bool isWave=(buf[0]==0x02 && buf[1]==0x0B);
                    if(isWave){ waveCount++; if(waveCount%8==1) Log("WAVE pkt#"+waveCount+" "+from.Port+">"+port+" len="+n); }
                    string p08=Find(buf,n,0x09,0x08);
                    if(p08!="-"){
                        string cur=p08+"|0900="+Find(buf,n,0x09,0x00)+"|0901="+Find(buf,n,0x09,0x01)+"|0902="+Find(buf,n,0x09,0x02);
                        if(cur!=last08){ Log("NUM  0908["+p08+"] 0900["+Find(buf,n,0x09,0x00)+"] 0901["+Find(buf,n,0x09,0x01)+"] 0902["+Find(buf,n,0x09,0x02)+"]  (in "+from.Port+">"+port+" t="+buf[0].ToString("X2")+buf[1].ToString("X2")+"/"+buf[12].ToString("X2")+buf[13].ToString("X2")+")"); last08=cur; }
                    }
                    if(n>=14 && buf[12]==0x04 && buf[13]==0x03) Log("<<< MON 0403 push  "+Hex(buf,n));
                    if(n>=14 && buf[0]==0x02 && (buf[13]==0x07||buf[13]==0x23)) Log(buf[12].ToString("X2")+buf[13].ToString("X2")+" resp       "+Hex(buf,n>48?48:n));
                }}catch{}
            }
            System.Threading.Thread.Sleep(5);
        }
        raw.Close();
        foreach(var s in socks)s.Close();
        Console.WriteLine("done -> "+path+"  (waveforms seen: "+waveCount+")");
    }
}
'@
"nibp-remote-start -> $Out  fire=$(-not $NoFire) kind0=$Kind0 payload=[$StartHex] settle=${SettleSec}s dur=${DurationSec}s"
[NibpStart]::Run($LocalIp,$MonitorIp,$waves,$payload,[byte]$Kind0,[byte]$KindLow,(-not $NoFire),$SettleSec,$DurationSec,$Out)
"done"
