# wpi

The goal was to create a command line version of [WPInternals](https://github.com/ReneLergner/WPinternals) in order to understand how to unlock the bootloader and "root" the Windows Phone OS of Nokia Lumia phones.  

> [!CAUTION]
> Only Lumia 520 is supported.

## General usage

To unlock the bootloader you have to set `--mode=UNLOCK` and pass 3 files in arguments:  
- An FFU file (it will be our first source to get the binaries of the partitions).
- An image file of an engineering SBL3 (the "engineering" version contains the codes required to boot in "Mass Storage" mode).
- A .hex file containing a programmer (we will use it to flash unsigned images of some partitions).

![](wpi01.png)

![](wpi02.png)

The optional parameter `--verbose` activates the display of some insights (for example the list of partitions of the GPT) but also all the data exchanged between the host computer and the phone (including the content of the flashed partitions). If you activate this option I advise to redirect the output into a file (**more than 35GB are logged when you unlock a phone !**).

Extract of _verbose_ log:
```
Read Flash application version (require 1.28 <= version < 2.0)...
< 4 bytes
4E 4F 4B 56                                               NOKV
> 91 bytes
4E 4F 4B 56 00 02 01 0F 01 1C 08 01 00 04 00 24 10 00 02  NOKV...........$...
00 04 00 24 00 00 03 00 04 00 E7 40 00 04 00 04 00 3B 50  ...$.......@.....;P
00 05 00 18 4E 6F 6B 69 61 2E 4D 53 4D 38 32 32 37 2E 50  ....Nokia.MSM8227.P
36 30 33 36 2E 31 2E 32 00 0D 00 02 00 01 0F 00 09 03 01  6036.1.2...........
01 01 00 00 01 01 FF 10 00 05 01 00 13 00 00              ...............
FlashApp:
	protocol: 1.15
	version: 1.28
Sub-blocks:
	ID 01
	ID 02
	ID 03
	ID 04
	ID 05
	ID 0D
	ID 0F
	ID 10

Read eMMC manufacturer (Samsung = risk of locking eMMC in read-only mode)...
< 11 bytes
4E 4F 4B 58 46 52 00 43 49 44 00                          NOKXFR.CID.
> 25 bytes
4E 4F 4B 58 46 52 00 00 00 43 49 44 00 00 00 00 08 00 90  NOKXFR...CID.......
00 4A 00 E3 92 E5                                         .J....
eMMC manufacturer: Hynix

Read Root Key Hash of the phone...
< 11 bytes
4E 4F 4B 58 46 52 00 52 52 4B 48                          NOKXFR.RRKH
> 49 bytes
4E 4F 4B 58 46 52 00 00 00 52 52 4B 48 00 00 00 20 F7 71  NOKXFR...RRKH... .q
E6 2A F8 99 94 06 4F 77 CD 3B C1 68 29 50 3B DF 9A 3D 50  .*....Ow.;.h)P;..=P
6D 3F AC EC AE F3 F8 08 C8 68 FD                          m?.......h.
Phone Root Hash Key (256 bits): F771E62AF89994064F77CD3BC16829503BDF9A3D506D3FACECAEF3F808C868FD
```

## Drivers required

We use USB to establish a communication between the host computer and the phone. And 3 [WinUSB](https://en.wikipedia.org/wiki/WinUSB) drives have to be installed in the host computer. Each of this driver is used at a different point of the unlock process.  
- WinUsbCompatIdInstaller.msi to communicate with the Windows Phone OS. The phone exposes the USB device VID_0421&PID_0661
- WinUsbDriversExt.msi to communicate with the UEFI applications. The phone exposes the USB device VID_0421&PID_066E
- EmergencyDownloadDriver.msi to communicate with the Primary Boot Loader and the Programmer. The phone exposes the USB device VID_05C6&PID_9008
