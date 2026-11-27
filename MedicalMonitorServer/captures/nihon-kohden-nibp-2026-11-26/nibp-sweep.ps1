# While registered as a CENTRAL, sweep one command: send type (SweepHi:SweepLo) with payload
# [val 00 00 00] for val in Start..Start+Count-1, one every Interval s, and record everything so we
# can see which val (if any) flips the NIBP measuring flag 0908 or draws a distinctive reply.
param(
    [string]$LocalIp='10.200.200.77',
    [string]$MonitorIp='10.10.46.48',
    [int]$SweepHi=0x04,
    [int]$SweepLo=0x01,
    [int]$KindLo=0x00,
    [int]$Start=0x00,
    [int]$Count=32,
    [double]$Interval=4,
    [string]$PayloadTailHex='00 00 00',
    [ValidateSet('payload','sub')][string]$Mode='payload',
    [string]$Out="$PSScriptRoot\sweep.raw.txt"
)
$tail = @($PayloadTailHex -split '[ ,]+' | Where-Object {$_ -ne ''} | ForEach-Object {[byte]('0x'+$_)})
Add-Type -TypeDefinition @'
using System;using System.IO;using System.Net;using System.Net.Sockets;using System.Text;
public static class NibpSweep{
 static string Hex(byte[] b,int n){var h=new StringBuilder(n*3);for(int i=0;i<n;i++)h.Append(b[i].ToString("X2")).Append(' ');return h.ToString();}
 static byte[] Msg(byte k,ushort seq,byte tHi,byte tLo,byte[] id,byte[] ip,byte[] pay){int len=32+pay.Length;var b=new byte[len];
  b[1]=k;b[2]=(byte)(seq>>8);b[3]=(byte)seq;b[4]=(byte)(len>>8);b[5]=(byte)len;b[7]=1;b[9]=1;b[12]=tHi;b[13]=tLo;b[16]=(byte)((len-12)>>8);b[17]=(byte)(len-12);id.CopyTo(b,24);ip.CopyTo(b,28);pay.CopyTo(b,32);return b;}
 public static void Run(string localIp,string monitorIp,byte sweepHi,byte sweepLo,byte kindLo,int start,int count,double interval,byte[] tail,string mode,string path){
  var local=IPAddress.Parse(localIp);var monitor=IPAddress.Parse(monitorIp);
  var a=local.GetAddressBytes();var m=monitor.GetAddressBytes();var monId=new byte[]{m[1],m[2],m[3],0x20};
  var beacon=new byte[64];new byte[]{0x04,0xFF,0,0,0,0x40,0,1,0,1,0,0,0x02,0x01,0,0,0,0x34}.CopyTo(beacon,0);
  beacon[24]=a[1];beacon[25]=a[2];beacon[26]=a[3];a.CopyTo(beacon,28);beacon[33]=0x01;Encoding.ASCII.GetBytes("CENTRAL").CopyTo(beacon,36);
  a.CopyTo(beacon,48);beacon[52]=a[1];beacon[53]=a[2];beacon[54]=a[3];beacon[55]=0x00;beacon[60]=0x01;beacon[61]=0x86;beacon[62]=0x01;
  var subscribe=new byte[]{4,0,0,0,0,0,1,0,8,0,1,1,1,0,0,0,0x55,0x55,0x55,0x7F,1,0,0,0,0x55,0x55,0x55,0x57};
  var bare=new byte[]{0x06,0xFF,0,0,0,0x0C,0,1,0,1,0,0};
  byte[][] reqs=new byte[][]{new byte[]{0x02,0x06,0x07},new byte[]{0x03,0x08,0x09},new byte[]{0x03,0x04,0x05},new byte[]{0x03,0x01,0x02},new byte[]{0x04,0x04,0x05},new byte[]{0x04,0x01,0x02},new byte[]{0x03,0x20,0x21}};
  var control=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);control.EnableBroadcast=true;control.Bind(new IPEndPoint(local,55405));
  var in8300=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);in8300.EnableBroadcast=true;in8300.Bind(new IPEndPoint(IPAddress.Any,8300));
  var in8301=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);in8301.Bind(new IPEndPoint(IPAddress.Any,8301));
  var socks=new[]{control,in8300,in8301};
  var bcast=new[]{new IPEndPoint(IPAddress.Parse("10.255.255.255"),8300),new IPEndPoint(IPAddress.Broadcast,8300)};
  var monCtl=new IPEndPoint(monitor,8300);
  var buf=new byte[65536];ushort seq=0x0700;var startT=DateTime.UtcNow;
  double seconds=10+count*interval+4;var end=startT.AddSeconds(seconds);
  var nextBeacon=startT;var nextSub=startT.AddSeconds(2);var nextReg=startT.AddSeconds(3);var lastStream=DateTime.MinValue;
  var sweepStart=startT.AddSeconds(8);var nextSweep=sweepStart;int idx=0;
  using(var raw=new StreamWriter(path,false){AutoFlush=true}){
   raw.WriteLine("# sweep type="+sweepHi.ToString("X2")+sweepLo.ToString("X2")+" val "+start+".."+(start+count-1)+" tail="+Hex(tail,tail.Length));
   while(DateTime.UtcNow<end){
    var now=DateTime.UtcNow;
    if(now>=nextBeacon){foreach(var t in bcast){try{control.SendTo(beacon,t);}catch{}}nextBeacon=now.AddSeconds(3);}
    if(now>=nextSub&&(now-lastStream).TotalSeconds>2){control.SendTo(Msg(0,++seq,0x02,0x22,monId,a,subscribe),monCtl);nextSub=now.AddSeconds(1);}
    if(now>=nextReg){foreach(var r in reqs){var pay=new byte[]{r[2],0,0,0};control.SendTo(Msg((byte)(r[0]==0x03?1:0),++seq,r[0],r[1],monId,a,pay),monCtl);}nextReg=now.AddSeconds(2);}
    if(now>=nextSweep&&idx<count){
     int val=start+idx;byte[] pkt;
     if(mode=="sub"){ pkt=Msg(kindLo,++seq,sweepHi,(byte)val,monId,a,tail); }
     else { var pay=new byte[1+tail.Length];pay[0]=(byte)val;tail.CopyTo(pay,1); pkt=Msg(kindLo,++seq,sweepHi,sweepLo,monId,a,pay); }
     control.SendTo(pkt,monCtl);
     raw.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff")+" SWEEP "+(mode=="sub"?("type="+sweepHi.ToString("X2")+val.ToString("X2")):("val=0x"+val.ToString("X2")))+" "+Hex(pkt,pkt.Length));
     idx++;nextSweep=now.AddSeconds(interval);
    }
    foreach(var s in socks){try{while(s.Available>0){EndPoint ep=new IPEndPoint(IPAddress.Any,0);int n=s.ReceiveFrom(buf,ref ep);var from=(IPEndPoint)ep;
      if(!from.Address.Equals(monitor))continue;if(n==64&&buf[0]==0x04)continue;int port=((IPEndPoint)s.LocalEndPoint).Port;
      if(buf[0]==0x0B||buf[0]==0x12)lastStream=DateTime.UtcNow;if(buf[0]==0x05)control.SendTo(bare,monCtl);
      raw.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff")+" IN "+from.Port+">"+port+" "+n+" "+Hex(buf,n));}}catch{}}
    System.Threading.Thread.Sleep(5);
   }
  }
  foreach(var s in socks)s.Close();
 }
}
'@
"sweep type=$('{0:X2}{1:X2}' -f $SweepHi,$SweepLo) val 0x$('{0:X2}' -f $Start)..0x$('{0:X2}' -f ($Start+$Count-1)) every ${Interval}s -> $Out"
[NibpSweep]::Run($LocalIp,$MonitorIp,[byte]$SweepHi,[byte]$SweepLo,[byte]$KindLo,$Start,$Count,$Interval,[byte[]]$tail,$Mode,$Out)
"done"
