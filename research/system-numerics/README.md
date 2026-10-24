# What System.Numerics.dll does

`artifacts/publish/Ultiviewer/System.Numerics.dll` (15 KB) contains **no code**. It is a *facade*: a stub
assembly that tells the .NET loader where the types it is named after really live. Its whole content is:

- an identity: `System.Numerics, Version=4.0.0.0, PublicKeyToken=b77a5c561934e089`, the name and version
  this assembly had in the old .NET Framework;
- three references: `System.Runtime`, `System.Runtime.Numerics` and `System.Numerics.Vectors`;
- **nine type forwarders**, which are the only part that matters;
- eighteen descriptive attributes (company, file version 10.0.1326.50504, "IsTrimmable", …).

Its only type is `<Module>`, which every assembly has, and it has no methods: 0 methods and 0 bytes of IL.

## The forwarding chain

```
                                  ┌─► System.Runtime.Numerics.dll (354 KB, real code)
 System.Numerics.dll (15 KB) ─────┤     BigInteger (220 methods), Complex (137 methods)
   no code, 9 forwarders          │
                                  └─► System.Numerics.Vectors.dll (15 KB, also no code)
                                        └─► System.Private.CoreLib.dll (real code)
                                              Vector2/3/4, Matrix3x2/4x4, Plane, Quaternion
```

| Type | Forwarded to | What it is |
|---|---|---|
| `BigInteger` | System.Runtime.Numerics | An integer of any size (arbitrary precision): `2^1000`, cryptography, exact factorials. |
| `Complex` | System.Runtime.Numerics | A complex number `a + bi` with arithmetic, `Abs`, `Exp`, `Sqrt`, trigonometry. |
| `Vector2`, `Vector3`, `Vector4` | System.Numerics.Vectors → CoreLib | 2-, 3- and 4-float vectors for graphics and games, accelerated with SIMD CPU instructions. |
| `Matrix3x2`, `Matrix4x4` | System.Numerics.Vectors → CoreLib | 2-D and 3-D transformation matrices: translate, rotate, scale, project. |
| `Quaternion` | System.Numerics.Vectors → CoreLib | A 3-D rotation without gimbal lock. |
| `Plane` | System.Numerics.Vectors → CoreLib | A 3-D plane (normal + distance), for geometry and collision tests. |

The vector types live in CoreLib, the runtime's core library, because the JIT compiler turns their operations
into single SIMD instructions; that only works when the types are part of the runtime itself.

## Why a file with no code exists

1. In the **.NET Framework** (the version built into Windows), these types were in `System.Numerics.dll`,
   version 4.0.0.0. Every library compiled against it records "type `Vector3` from assembly `System.Numerics`".
2. **Modern .NET** reorganized its libraries: the code moved to `System.Runtime.Numerics` and CoreLib.
3. So that old libraries still run unchanged, .NET ships a `System.Numerics.dll` under the old name with type
   forwarders. When such a library uses `Vector3`, the loader:
   1. opens `System.Numerics.dll`;
   2. finds the ExportedType row "Vector3 → System.Numerics.Vectors" and opens that;
   3. finds "Vector3 → System.Private.CoreLib" there and gets the real type.

   This happens once per type; afterwards the result is cached.

`System.Runtime.dll` is the biggest facade of this kind: 936 forwarders and no code. The ~210 `System.*.dll`
files in the publish folder are mostly such facades or small libraries. They exist because .NET's public API is
split into many small reference assemblies, while the implementation lives in a few large ones.

**Does Ultiviewer need it?** Not directly. Neither Ultiviewer nor any of its libraries reference
`System.Numerics`. AngleSharp references `System.Numerics.Vectors`, the second stub, and SQLite's core
references `netstandard`, the general compatibility facade. The file is in the folder only because a
self-contained build copies the entire framework.

## Can it be deleted?

It can be deleted, but it shouldn't be. The checks:

- **Nothing uses it.** Of the 270 DLLs in the publish folder, two reference `System.Numerics`:
  `DirectWriteForwarder.dll` (WPF's text renderer) and `System.Printing.dll`. Both are C++/CLI assemblies, and
  the inspector's "Types used from each reference" section shows that neither uses a single type from it. The
  C++/CLI compiler adds the reference by default.
- **The app still starts.** A self-contained .NET 10 test app with `System.Numerics.dll` removed still starts,
  even though `deps.json` lists the file, and `Vector3` works normally. Only an old-style lookup,
  `Type.GetType("System.Numerics.Vector3, System.Numerics")`, fails with `FileNotFoundException`, and only at
  that moment.

It isn't worth doing, though:

- It saves 15 KB of 250 MB.
- The next publish puts it back.
- A plugin or library built for the .NET Framework would fail only when it first touched one of these types.

To shrink the folder, change the build instead. English-only satellite resources removes 221 files, a
single-file publish packs everything into one exe, and a framework-dependent build removes the runtime. WPF
apps can't use .NET's trimmer, which is what would safely remove unused files like this one.

## Anatomy of the file

`numerics_inspector.cpp` reads the file with plain C++ and no libraries, and prints each part. Its sections
follow the file's layers:

1. **PE container (Windows executable format).**
   - **Header.** An `MZ` DOS header points to the `PE` header: machine i386, PE32, a DLL. It has three
     sections: `.text` (metadata), `.rsrc` (the Windows version resource that Explorer's "Details" tab shows)
     and `.reloc`.
   - **Loader stub.** The only import is `mscoree.dll!_CorDllMain`, and the entry point is a 6-byte stub
     `FF 25 00 20 00 10` (`jmp [_CorDllMain]`). This is how Windows XP started .NET DLLs; modern .NET never
     executes it.
2. **CLI header.** Data directory 14 marks the file as .NET.
   - **Flags.** `ILONLY | STRONGNAMESIGNED`. An i386 machine type plus ILONLY means "AnyCPU": the same file
     works in 32- and 64-bit processes. There is no ReadyToRun (precompiled native) code; there is nothing to
     compile.
   - **Strong-name signature.** 128 bytes: an RSA signature by Microsoft.
3. **Metadata.** The `BSJB` root lists the streams `#~` (tables), `#Strings`, `#GUID` and `#Blob`. The tables
   are fixed-size rows whose index columns are 2 or 4 bytes wide depending on the row counts. That is why the
   program computes the layout of all 45 ECMA-335 tables. This file has only 8 of them:

   | Table | Rows | Holds |
   |---|---|---|
   | Module | 1 | The module's name. |
   | TypeDef | 1 | `<Module>` only. |
   | Assembly | 1 | The file's identity. |
   | AssemblyRef | 3 | The three targets. |
   | ExportedType | 9 | The forwarders, flag `0x00200000`. |
   | TypeRef, MemberRef, CustomAttribute | 15, 14, 18 | The attributes' types and constructors, and the attribute values. |
4. **Identity and the ECMA key.** The 16-byte public key `00000000000000000400000000000000` is not a real
   RSA key. It is the ECMA standard placeholder, which the runtime maps to Microsoft's key. The token is the
   last 8 bytes of its SHA-1 hash in reverse order, `b77a5c561934e089`, the token every .NET Framework
   system assembly carried. The program computes it with its own SHA-1. The same calculation on
   System.Runtime.Numerics's 160-byte RSA key gives Microsoft's other well-known token, `b03f5f7f11d50a3a`.

**Verification.** The program's output was compared with a dump by .NET's own `System.Reflection.Metadata`:
- **System.Numerics.dll and System.Numerics.Vectors.dll:** tables, row counts, references, forwarders and
  attribute values are identical.
- **System.Runtime.Numerics.dll:** the program also reads this full, precompiled 64-bit assembly (25 tables,
  35 types, 632 methods) correctly.

## The same idea in native C++

Native Windows DLLs have the same mechanism: *forwarded exports*. Windows uses it widely. For example,
`kernel32.dll` forwards many functions to `kernelbase.dll`. A native DLL equivalent to System.Numerics.dll
would contain no functions at all, only forwarding entries in its export table:

```cpp
// numerics_facade.cpp: a native DLL that implements nothing and forwards everything.
// Build (MSVC): cl /LD numerics_facade.cpp /link /NOENTRY
// Each pragma adds an export whose address is "OtherDll.Function"; the Windows loader resolves it
// to the function in the other DLL, just as .NET resolves an ExportedType row.
#pragma comment(linker, "/export:BigInteger_Add=RuntimeNumerics.BigInteger_Add")
#pragma comment(linker, "/export:Complex_Abs=RuntimeNumerics.Complex_Abs")
#pragma comment(linker, "/export:Vector3_Dot=NumericsVectors.Vector3_Dot")
```

The difference: Windows forwards individual *functions* by name, while .NET forwards whole *types* (with all
their methods, fields and interfaces) by namespace and name.

## Running the inspector

```bash
g++ -std=c++17 -O2 -Wall -Wextra -o numerics_inspector numerics_inspector.cpp   # Linux/WSL, or MinGW / clang
cl /std:c++17 /EHsc /O2 numerics_inspector.cpp                                  # Visual Studio
./numerics_inspector ../../artifacts/publish/Ultiviewer/System.Numerics.dll
```

It accepts any .NET assembly. Try `System.Runtime.dll` (936 forwarders) or `Ultiviewer.Core.dll`.
