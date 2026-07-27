# TextmapParser
A proof-of-concept TextMap parser for some anime game

# Current status
- Tested on 6.6.0, 6.7.0 and 6.7.5x
- Works straight out of the box besides the RVA, which you need to override via arguments
- TODO: use DummyDLL and add arguments for Data/module paths

# Requirements
- Visual Studio 2026
- .NET SDK 10.0 & .NET 10.0 Build TOols
- At least 1 brain cell

# I DO NOT CLAIM ANY RESPONSIBILITY FOR ANY USAGE OF THIS SOFTWARE, THE SOFTWARE IS MADE 100% FOR EDUCATIONAL PURPOSES ONLY

How does it work?
- The tool disassembles and analyzes the Textmap loader
- Then the tool creates a plan for decryption
- Then it just decrypts and parses the raw binary 
- ..and outputs the data in JSON format

Copyright © Hiro420