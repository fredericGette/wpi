# wpi

Work-In-Progress  
The goal is to create a command line version of [WPInternals](https://github.com/ReneLergner/WPinternals) in order to understand how to unlock the bootloader and "root" the OS of Nokia Lumia phones.  

> [!CAUTION]
> Only Lumia 520 is supported.

To unlock the bootloader you have to pass 3 files in arguments:  
- An FFU file (it will be our first source to get the binaries of the partitions).
- An image file of an engineering SBL3 (the "engineering" version contains the codes required to boot in "Mass Storage" mode).
- A .hex file containing a programmer (we will use it to flash unsigned images of some partitions).

![](wpi01.png)

![](wpi02.png)
