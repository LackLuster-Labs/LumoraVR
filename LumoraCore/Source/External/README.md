## External

This folder contains code that is to interface with large external librarys for example a audio mixing or file compression
anyting in this folder should be genralizable to whaever class of interaction it related to;
for example 

you could have `` Compression.ICompressable ``

and `` 7ZCompressable : ICompressable ``

and `` ZipCopmressable : ICompressable ``

the file structure and namespace structure should be.
```
External/Compression/ICompressable.cs
Lumora.Core.External.Compression

External/Compression/7zip/7ZCompressable.cs
Lumora.Core.External.Compression.7zip
```
sub categories are also allowed 
like `` External/Comunication/osc 
     External/Comunication/midi
     ``


