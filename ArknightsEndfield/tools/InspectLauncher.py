import struct, pathlib, sys
p=pathlib.Path(sys.argv[1]); b=p.read_bytes()
u16=lambda o:struct.unpack_from('<H',b,o)[0]
u32=lambda o:struct.unpack_from('<I',b,o)[0]
pe=u32(0x3c); n=u16(pe+6); opt=pe+24; magic=u16(opt); base=struct.unpack_from('<Q' if magic==0x20b else '<I',b,opt+(24 if magic==0x20b else 28))[0]
sections=[]
for i in range(n):
 o=opt+u16(pe+20)+i*40; sections.append((b[o:o+8].rstrip(b'\0').decode(),u32(o+12),u32(o+8),u32(o+20),u32(o+16)))
def rva(o):
 for name,va,vs,raw,size in sections:
  if raw<=o<raw+size:return va+o-raw
needles=[b'--game\0',b'--reason\0',b'--oldVer\0',b'--key\0',b'CCmdLineConfig::_Init\0']
targets={}
for needle in needles:
 start=0
 while (o:=b.find(needle,start))>=0:
  if rva(o)!=None:targets[rva(o)]=needle.decode().strip('\0')
  start=o+1
functions=[]
for name,va,vs,raw,size in sections:
 if name=='.pdata':
  for o in range(raw,raw+size-11,12):
   begin,end,uw=struct.unpack_from('<III',b,o)
   if begin and end: functions.append((begin,end))
for name,va,vs,raw,size in sections:
 if name!='.text':continue
 for o in range(raw,raw+size-7):
  if b[o] in (0x48,0x4c) and b[o+1]==0x8d and b[o+2]&0xc7==5:
   at=va+o-raw; dest=at+7+struct.unpack_from('<i',b,o+3)[0]
   if dest in targets:
    fn=next(((a,z) for a,z in functions if a<=at<z),None)
    print(targets[dest],hex(base+at),'function',tuple(hex(base+x) for x in fn) if fn else '?')
print('machine',hex(u16(pe+4)),'base',hex(base))
