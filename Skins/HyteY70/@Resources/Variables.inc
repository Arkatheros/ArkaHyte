[Variables]
; Case screen (portrait orientation in Windows)
ScreenW=682
ScreenH=2560
PadX=40
ContentW=602
BarH=14

; Palette
ColorBg=0,0,0,255
ColorCard=16,16,22,255
ColorText=235,235,240,255
ColorDim=125,125,140,255
ColorTrack=38,38,48,255
ColorCpu=0,200,255,255
ColorGpu=118,255,120,255
ColorRam=255,170,40,255
ColorNet=200,120,255,255
ColorDisk=255,90,120,255
FontFace=Segoe UI

; Vertical anchor of each card
ClockY=80
CpuY=470
GpuY=850
RamY=1230
NetY=1530
DiskY=1830
HistoryY=2150
FooterY=2480

; HWiNFO Gadget indices: HKCU\Software\HWiNFO64\VSB\ValueRawN
; N follows the order in which sensors were ticked "Report value in Gadget" in HWiNFO.
HwCpuTemp=0
HwCpuPower=1
HwGpuTemp=2
HwGpuPower=3
HwGpuLoad=4
HwGpuVram=5
; HWiNFO only exposes VRAM usage as a percentage, converted with the card capacity
GpuVramTotalGb=24

; Drives shown in the storage card
Drive1=C:
Drive2=D:
