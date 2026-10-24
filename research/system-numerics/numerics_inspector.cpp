// numerics_inspector.cpp
//
// A from-scratch reader for .NET assemblies, written to reverse engineer System.Numerics.dll (shipped in
// artifacts/publish/Ultiviewer). It depends on nothing but the C++17 standard library, so every byte it
// interprets is spelled out here. Two specifications describe the file:
//
//   * Microsoft PE/COFF: the Windows executable container (DOS stub, PE headers, sections, imports).
//   * ECMA-335 Partition II ("Common Language Infrastructure"): the .NET part inside it, the CLI header
//     (§II.25.3.3), the metadata root and its streams (§II.24.2), and the metadata tables (§II.22).
//
// Build and run (any OS):
//     g++ -std=c++17 -O2 -Wall -Wextra -o numerics_inspector numerics_inspector.cpp
//     ./numerics_inspector System.Numerics.dll
//
// What it finds is explained in README.md next to this file. In short: System.Numerics.dll contains no code.
// It is a "facade": its ExportedType table forwards nine types (BigInteger, Complex, Vector2/3/4, Matrix3x2,
// Matrix4x4, Plane, Quaternion) to the assemblies that really implement them.

#include <array>
#include <cstdint>
#include <cstdio>
#include <fstream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <vector>

namespace {

// ---------------------------------------------------------------------------------------------------------
// Little-endian byte access. PE files and .NET metadata are always little-endian.
// ---------------------------------------------------------------------------------------------------------

struct Bytes {
    std::vector<uint8_t> data;

    void need(size_t offset, size_t length) const {
        if (offset > data.size() || length > data.size() - offset) {
            throw std::runtime_error("read past the end of the file at offset " + std::to_string(offset));
        }
    }
    uint8_t u8(size_t o) const { need(o, 1); return data[o]; }
    uint16_t u16(size_t o) const { need(o, 2); return uint16_t(data[o] | data[o + 1] << 8); }
    uint32_t u32(size_t o) const { need(o, 4); return uint32_t(data[o]) | uint32_t(data[o + 1]) << 8 | uint32_t(data[o + 2]) << 16 | uint32_t(data[o + 3]) << 24; }
    uint64_t u64(size_t o) const { return uint64_t(u32(o)) | uint64_t(u32(o + 4)) << 32; }

    // A NUL-terminated ASCII/UTF-8 string starting at o.
    std::string cstr(size_t o) const {
        std::string s;
        while (u8(o) != 0) s += char(data[o++]);
        return s;
    }
};

std::string hex(const uint8_t* p, size_t n) {
    static const char* digits = "0123456789abcdef";
    std::string s;
    for (size_t i = 0; i < n; i++) { s += digits[p[i] >> 4]; s += digits[p[i] & 15]; }
    return s;
}

std::string hex32(uint32_t v) { char b[16]; std::snprintf(b, sizeof b, "0x%X", v); return b; }

// ---------------------------------------------------------------------------------------------------------
// SHA-1 (FIPS 180-4), only to show how a strong-name "public key token" is derived from a public key:
// token = the last 8 bytes of SHA-1(public key), in reverse order.
// ---------------------------------------------------------------------------------------------------------

std::array<uint8_t, 20> sha1(const std::vector<uint8_t>& message) {
    uint32_t h[5] = {0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0};
    std::vector<uint8_t> m = message;
    const uint64_t bits = uint64_t(message.size()) * 8;
    m.push_back(0x80);
    while (m.size() % 64 != 56) m.push_back(0);
    for (int i = 7; i >= 0; i--) m.push_back(uint8_t(bits >> (i * 8)));
    auto rol = [](uint32_t x, int n) { return (x << n) | (x >> (32 - n)); };
    for (size_t chunk = 0; chunk < m.size(); chunk += 64) {
        uint32_t w[80];
        for (int i = 0; i < 16; i++) {
            w[i] = uint32_t(m[chunk + 4 * i]) << 24 | uint32_t(m[chunk + 4 * i + 1]) << 16 | uint32_t(m[chunk + 4 * i + 2]) << 8 | m[chunk + 4 * i + 3];
        }
        for (int i = 16; i < 80; i++) w[i] = rol(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
        for (int i = 0; i < 80; i++) {
            uint32_t f, k;
            if (i < 20)      { f = (b & c) | (~b & d);          k = 0x5A827999; }
            else if (i < 40) { f = b ^ c ^ d;                   k = 0x6ED9EBA1; }
            else if (i < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC; }
            else             { f = b ^ c ^ d;                   k = 0xCA62C1D6; }
            const uint32_t t = rol(a, 5) + f + e + k + w[i];
            e = d; d = c; c = rol(b, 30); b = a; a = t;
        }
        h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e;
    }
    std::array<uint8_t, 20> out{};
    for (int i = 0; i < 20; i++) out[i] = uint8_t(h[i / 4] >> (24 - 8 * (i % 4)));
    return out;
}

// ---------------------------------------------------------------------------------------------------------
// Part 1: the PE/COFF container.
//
// Every Windows executable (.exe, .dll) starts with a 64-byte DOS header ("MZ"). Its field e_lfanew (offset
// 0x3C) points to the "PE\0\0" signature, followed by the COFF file header and the "optional" header (which
// is not optional for images). The optional header ends with 16 data directories, (RVA, size) pairs that
// locate well-known structures: [1] imports, [2] resources, [4] Authenticode signature, [5] base
// relocations, [14] the CLI header, which makes the file a .NET assembly.
//
// RVAs ("relative virtual addresses") are addresses once the file is mapped in memory; the section table
// maps them back to file offsets.
// ---------------------------------------------------------------------------------------------------------

struct Section { std::string name; uint32_t va, vsize, rawPtr, rawSize; };

struct PeImage {
    Bytes file;
    bool pe32Plus = false;
    uint16_t machine = 0, characteristics = 0, subsystem = 0, dllCharacteristics = 0;
    uint32_t entryPoint = 0;
    std::vector<std::pair<uint32_t, uint32_t>> directories;   // (RVA, size)
    std::vector<Section> sections;

    size_t offsetOf(uint32_t rva) const {
        for (const auto& s : sections) {
            if (rva >= s.va && rva < s.va + std::max(s.vsize, s.rawSize)) return s.rawPtr + (rva - s.va);
        }
        throw std::runtime_error("RVA " + hex32(rva) + " is in no section");
    }
};

PeImage readPe(const std::string& path) {
    PeImage pe;
    std::ifstream in(path, std::ios::binary);
    if (!in) throw std::runtime_error("cannot open " + path);
    pe.file.data.assign(std::istreambuf_iterator<char>(in), {});
    const Bytes& f = pe.file;

    if (f.u16(0) != 0x5A4D) throw std::runtime_error("no MZ signature: not a Windows executable");
    const uint32_t peOffset = f.u32(0x3C);
    if (f.u32(peOffset) != 0x00004550) throw std::runtime_error("no PE signature");

    // COFF file header (20 bytes).
    const size_t coff = peOffset + 4;
    pe.machine = f.u16(coff);                      // 0x14C = i386, 0x8664 = x64
    const uint16_t sectionCount = f.u16(coff + 2);
    const uint16_t optionalSize = f.u16(coff + 16);
    pe.characteristics = f.u16(coff + 18);

    // Optional header: 0x10B = PE32 (32-bit layout), 0x20B = PE32+ (64-bit layout).
    const size_t opt = coff + 20;
    pe.pe32Plus = f.u16(opt) == 0x20B;
    pe.entryPoint = f.u32(opt + 16);
    pe.subsystem = f.u16(opt + 68);
    pe.dllCharacteristics = f.u16(opt + 70);
    const size_t dirCountAt = opt + (pe.pe32Plus ? 108 : 92);
    const uint32_t dirCount = f.u32(dirCountAt);
    for (uint32_t i = 0; i < dirCount; i++) pe.directories.emplace_back(f.u32(dirCountAt + 4 + 8 * i), f.u32(dirCountAt + 8 + 8 * i));

    // Section table: 40 bytes per section.
    const size_t sectionTable = opt + optionalSize;
    for (uint16_t i = 0; i < sectionCount; i++) {
        const size_t s = sectionTable + 40 * size_t(i);
        std::string name;
        for (int c = 0; c < 8 && f.u8(s + c) != 0; c++) name += char(f.u8(s + c));
        pe.sections.push_back({name, f.u32(s + 12), f.u32(s + 8), f.u32(s + 20), f.u32(s + 16)});
    }
    return pe;
}

// ---------------------------------------------------------------------------------------------------------
// Part 2: ECMA-335 metadata.
//
// Data directory 14 points to the CLI header (IMAGE_COR20_HEADER, §II.25.3.3). It locates the metadata
// root ("BSJB", §II.24.2.1), which lists streams:
//   #~        the metadata tables (rows of fixed-size records)
//   #Strings  UTF-8 identifiers (type and member names), addressed by byte offset
//   #US       user strings (string literals used by IL code)
//   #GUID     16-byte GUIDs (the module version id), addressed by 1-based index
//   #Blob     length-prefixed binary data (signatures, public keys, attribute values)
// ---------------------------------------------------------------------------------------------------------

// The 45 metadata tables of ECMA-335 §II.22, in table-number order.
enum Table {
    Module, TypeRef, TypeDef, FieldPtr, Field, MethodPtr, MethodDef, ParamPtr, Param, InterfaceImpl,
    MemberRef, Constant, CustomAttribute, FieldMarshal, DeclSecurity, ClassLayout, FieldLayout, StandAloneSig,
    EventMap, EventPtr, Event, PropertyMap, PropertyPtr, Property, MethodSemantics, MethodImpl, ModuleRef,
    TypeSpec, ImplMap, FieldRVA, EncLog, EncMap, Assembly, AssemblyProcessor, AssemblyOS, AssemblyRef,
    AssemblyRefProcessor, AssemblyRefOS, File, ExportedType, ManifestResource, NestedClass, GenericParam,
    MethodSpec, GenericParamConstraint, TableCount
};

const char* const TableNames[TableCount] = {
    "Module", "TypeRef", "TypeDef", "FieldPtr", "Field", "MethodPtr", "MethodDef", "ParamPtr", "Param", "InterfaceImpl",
    "MemberRef", "Constant", "CustomAttribute", "FieldMarshal", "DeclSecurity", "ClassLayout", "FieldLayout", "StandAloneSig",
    "EventMap", "EventPtr", "Event", "PropertyMap", "PropertyPtr", "Property", "MethodSemantics", "MethodImpl", "ModuleRef",
    "TypeSpec", "ImplMap", "FieldRVA", "EncLog", "EncMap", "Assembly", "AssemblyProcessor", "AssemblyOS", "AssemblyRef",
    "AssemblyRefProcessor", "AssemblyRefOS", "File", "ExportedType", "ManifestResource", "NestedClass", "GenericParam",
    "MethodSpec", "GenericParamConstraint",
};

// A coded index (§II.24.2.6) packs "which table" into its low bits and the row number into the rest.
// It takes 2 bytes when every candidate table is small enough for the row number to fit beside the tag.
struct Coded { int tagBits; std::vector<int> tables; };   // -1 = unused tag value
const Coded TypeDefOrRef{2, {TypeDef, TypeRef, TypeSpec}};
const Coded HasConstant{2, {Field, Param, Property}};
const Coded HasCustomAttribute{5, {MethodDef, Field, TypeRef, TypeDef, Param, InterfaceImpl, MemberRef, Module, DeclSecurity,
                                   Property, Event, StandAloneSig, ModuleRef, TypeSpec, Assembly, AssemblyRef, File,
                                   ExportedType, ManifestResource, GenericParam, GenericParamConstraint, MethodSpec}};
const Coded HasFieldMarshal{1, {Field, Param}};
const Coded HasDeclSecurity{2, {TypeDef, MethodDef, Assembly}};
const Coded MemberRefParent{3, {TypeDef, TypeRef, ModuleRef, MethodDef, TypeSpec}};
const Coded HasSemantics{1, {Event, Property}};
const Coded MethodDefOrRef{1, {MethodDef, MemberRef}};
const Coded MemberForwarded{1, {Field, MethodDef}};
const Coded Implementation{2, {File, AssemblyRef, ExportedType}};
const Coded CustomAttributeType{3, {-1, -1, MethodDef, MemberRef, -1}};
const Coded ResolutionScope{2, {Module, ModuleRef, AssemblyRef, TypeRef}};
const Coded TypeOrMethodDef{1, {TypeDef, MethodDef}};

// One column of a table: a fixed width, a heap index, a plain table index, or a coded index.
struct Column {
    enum Kind { Fixed, StringHeap, GuidHeap, BlobHeap, Index, CodedIndex } kind;
    int width = 0;             // Fixed
    int table = 0;             // Index
    const Coded* coded = nullptr;
};
Column fixed(int width) { return {Column::Fixed, width}; }
Column str() { return {Column::StringHeap}; }
Column guid() { return {Column::GuidHeap}; }
Column blob() { return {Column::BlobHeap}; }
Column idx(int table) { Column c{Column::Index}; c.table = table; return c; }
Column coded(const Coded& k) { Column c{Column::CodedIndex}; c.coded = &k; return c; }

// The column layout of every table (§II.22.2 – §II.22.39).
std::vector<Column> schema(int table) {
    switch (table) {
        case Module: return {fixed(2), str(), guid(), guid(), guid()};                       // Generation, Name, Mvid, EncId, EncBaseId
        case TypeRef: return {coded(ResolutionScope), str(), str()};                         // ResolutionScope, Name, Namespace
        case TypeDef: return {fixed(4), str(), str(), coded(TypeDefOrRef), idx(Field), idx(MethodDef)};
        case FieldPtr: return {idx(Field)};
        case Field: return {fixed(2), str(), blob()};
        case MethodPtr: return {idx(MethodDef)};
        case MethodDef: return {fixed(4), fixed(2), fixed(2), str(), blob(), idx(Param)};    // RVA, ImplFlags, Flags, Name, Signature, ParamList
        case ParamPtr: return {idx(Param)};
        case Param: return {fixed(2), fixed(2), str()};
        case InterfaceImpl: return {idx(TypeDef), coded(TypeDefOrRef)};
        case MemberRef: return {coded(MemberRefParent), str(), blob()};                      // Class, Name, Signature
        case Constant: return {fixed(2), coded(HasConstant), blob()};                        // Type (+1 padding byte), Parent, Value
        case CustomAttribute: return {coded(HasCustomAttribute), coded(CustomAttributeType), blob()};  // Parent, Type (constructor), Value
        case FieldMarshal: return {coded(HasFieldMarshal), blob()};
        case DeclSecurity: return {fixed(2), coded(HasDeclSecurity), blob()};
        case ClassLayout: return {fixed(2), fixed(4), idx(TypeDef)};
        case FieldLayout: return {fixed(4), idx(Field)};
        case StandAloneSig: return {blob()};
        case EventMap: return {idx(TypeDef), idx(Event)};
        case EventPtr: return {idx(Event)};
        case Event: return {fixed(2), str(), coded(TypeDefOrRef)};
        case PropertyMap: return {idx(TypeDef), idx(Property)};
        case PropertyPtr: return {idx(Property)};
        case Property: return {fixed(2), str(), blob()};
        case MethodSemantics: return {fixed(2), idx(MethodDef), coded(HasSemantics)};
        case MethodImpl: return {idx(TypeDef), coded(MethodDefOrRef), coded(MethodDefOrRef)};
        case ModuleRef: return {str()};
        case TypeSpec: return {blob()};
        case ImplMap: return {fixed(2), coded(MemberForwarded), str(), idx(ModuleRef)};
        case FieldRVA: return {fixed(4), idx(Field)};
        case EncLog: return {fixed(4), fixed(4)};
        case EncMap: return {fixed(4)};
        case Assembly: return {fixed(4), fixed(2), fixed(2), fixed(2), fixed(2), fixed(4), blob(), str(), str()};  // HashAlg, Major..Revision, Flags, PublicKey, Name, Culture
        case AssemblyProcessor: return {fixed(4)};
        case AssemblyOS: return {fixed(4), fixed(4), fixed(4)};
        case AssemblyRef: return {fixed(2), fixed(2), fixed(2), fixed(2), fixed(4), blob(), str(), str(), blob()};  // Version, Flags, PublicKeyOrToken, Name, Culture, HashValue
        case AssemblyRefProcessor: return {fixed(4), idx(AssemblyRef)};
        case AssemblyRefOS: return {fixed(4), fixed(4), fixed(4), idx(AssemblyRef)};
        case File: return {fixed(4), str(), blob()};
        case ExportedType: return {fixed(4), fixed(4), str(), str(), coded(Implementation)};   // Flags, TypeDefId, Name, Namespace, Implementation
        case ManifestResource: return {fixed(4), fixed(4), str(), coded(Implementation)};
        case NestedClass: return {idx(TypeDef), idx(TypeDef)};
        case GenericParam: return {fixed(2), fixed(2), coded(TypeOrMethodDef), str()};
        case MethodSpec: return {coded(MethodDefOrRef), blob()};
        case GenericParamConstraint: return {idx(GenericParam), coded(TypeDefOrRef)};
    }
    return {};
}

struct Metadata {
    Bytes const* file = nullptr;
    std::string version;                       // e.g. "v4.0.30319"
    size_t strings = 0, blobs = 0, guids = 0;  // file offsets of the heaps
    bool wideStrings = false, wideGuids = false, wideBlobs = false;
    std::array<uint32_t, TableCount> rows{};
    std::array<size_t, TableCount> tableStart{};
    std::array<std::vector<int>, TableCount> columnWidths;
    std::array<int, TableCount> rowSize{};

    int width(const Column& c) const {
        switch (c.kind) {
            case Column::Fixed: return c.width;
            case Column::StringHeap: return wideStrings ? 4 : 2;
            case Column::GuidHeap: return wideGuids ? 4 : 2;
            case Column::BlobHeap: return wideBlobs ? 4 : 2;
            case Column::Index: return rows[c.table] < 0x10000 ? 2 : 4;
            case Column::CodedIndex: {
                uint32_t largest = 0;
                for (int t : c.coded->tables) if (t >= 0) largest = std::max(largest, rows[t]);
                return largest < (1u << (16 - c.coded->tagBits)) ? 2 : 4;
            }
        }
        return 0;
    }

    // The value in column `col` of 1-based row `row` of `table`.
    uint32_t get(int table, uint32_t row, int col) const {
        size_t at = tableStart[table] + size_t(row - 1) * rowSize[table];
        for (int i = 0; i < col; i++) at += columnWidths[table][i];
        return columnWidths[table][col] == 2 ? file->u16(at) : columnWidths[table][col] == 1 ? file->u8(at) : file->u32(at);
    }

    std::string string(uint32_t offset) const { return file->cstr(strings + offset); }

    // A #Blob entry: a compressed length (§II.23.2: 1, 2 or 4 bytes, by the top bits of the first byte), then bytes.
    std::vector<uint8_t> blob(uint32_t offset) const {
        size_t at = blobs + offset;
        const uint8_t first = file->u8(at);
        uint32_t length;
        if ((first & 0x80) == 0) { length = first; at += 1; }
        else if ((first & 0xC0) == 0x80) { length = (first & 0x3F) << 8 | file->u8(at + 1); at += 2; }
        else { length = (first & 0x1F) << 24 | file->u8(at + 1) << 16 | file->u8(at + 2) << 8 | file->u8(at + 3); at += 4; }
        file->need(at, length);
        return {file->data.begin() + at, file->data.begin() + at + length};
    }

    // Splits a coded index into (table, 1-based row).
    std::pair<int, uint32_t> decode(const Coded& k, uint32_t value) const {
        const uint32_t tag = value & ((1u << k.tagBits) - 1);
        const int table = tag < k.tables.size() ? k.tables[tag] : -1;
        return {table, value >> k.tagBits};
    }
};

Metadata readMetadata(const PeImage& pe) {
    const Bytes& f = pe.file;
    if (pe.directories.size() <= 14 || pe.directories[14].first == 0) {
        throw std::runtime_error("no CLI header (data directory 14): this is a native DLL, not a .NET assembly");
    }

    // CLI header: cb, runtime version, MetaData (RVA, size), Flags, EntryPointToken, Resources, StrongNameSignature, ...
    const size_t cli = pe.offsetOf(pe.directories[14].first);
    const size_t root = pe.offsetOf(f.u32(cli + 8));

    Metadata md;
    md.file = &f;
    if (f.u32(root) != 0x424A5342) throw std::runtime_error("no BSJB metadata signature");   // "BSJB": the initials of its designers
    const uint32_t versionLength = f.u32(root + 12);
    md.version = f.cstr(root + 16);
    size_t at = root + 16 + versionLength;
    const uint16_t streamCount = f.u16(at + 2);
    at += 4;

    size_t tables = 0;
    for (uint16_t i = 0; i < streamCount; i++) {
        const uint32_t offset = f.u32(at), size = f.u32(at + 4);
        const std::string name = f.cstr(at + 8);
        at += 8 + ((name.size() + 4) & ~size_t(3));    // the name is padded to a multiple of 4 bytes
        (void)size;
        if (name == "#~" || name == "#-") tables = root + offset;
        else if (name == "#Strings") md.strings = root + offset;
        else if (name == "#Blob") md.blobs = root + offset;
        else if (name == "#GUID") md.guids = root + offset;
    }

    // The #~ stream: a header, one 32-bit row count per present table, then the tables back to back.
    const uint8_t heapSizes = f.u8(tables + 6);
    md.wideStrings = heapSizes & 1;
    md.wideGuids = heapSizes & 2;
    md.wideBlobs = heapSizes & 4;
    const uint64_t valid = f.u64(tables + 8);           // bit n set = table n is present
    at = tables + 24;
    for (int t = 0; t < 64; t++) {
        if (valid >> t & 1) {
            if (t >= TableCount) throw std::runtime_error("unknown metadata table " + std::to_string(t));
            md.rows[t] = f.u32(at);
            at += 4;
        }
    }
    if (heapSizes & 0x40) at += 4;                      // an extra 32-bit value in edit-and-continue builds

    // Row sizes depend on the row counts (2- or 4-byte indexes), so they are computed, not fixed.
    for (int t = 0; t < TableCount; t++) {
        for (const Column& c : schema(t)) md.columnWidths[t].push_back(md.width(c));
        for (int w : md.columnWidths[t]) md.rowSize[t] += w;
        md.tableStart[t] = at;
        at += size_t(md.rows[t]) * md.rowSize[t];
    }
    return md;
}

// ---------------------------------------------------------------------------------------------------------
// Part 3: custom attribute values (§II.23.3). The value blob starts with the prolog 0x0001, then the
// constructor's arguments in the types its signature declares, then the named property/field arguments.
// ---------------------------------------------------------------------------------------------------------

struct Reader {
    const std::vector<uint8_t>& b;
    size_t at = 0;
    uint8_t u8() { if (at >= b.size()) throw std::runtime_error("short blob"); return b[at++]; }
    uint16_t u16() { uint16_t v = u8(); return uint16_t(v | u8() << 8); }
    uint32_t u32() { uint32_t v = u16(); return v | uint32_t(u16()) << 16; }
    uint32_t compressed() {
        const uint8_t first = u8();
        if ((first & 0x80) == 0) return first;
        if ((first & 0xC0) == 0x80) return (first & 0x3F) << 8 | u8();
        uint32_t v = (first & 0x1F) << 24; v |= u8() << 16; v |= u8() << 8; return v | u8();
    }
    std::string serString() {               // 0xFF = null, else a compressed length and UTF-8 bytes
        if (at < b.size() && b[at] == 0xFF) { at++; return "null"; }
        const uint32_t n = compressed();
        if (at + n > b.size()) throw std::runtime_error("short string");
        std::string s(b.begin() + at, b.begin() + at + n);
        at += n;
        return "\"" + s + "\"";
    }
};

// Reads one value of an element type (§II.23.1.16): 0x02 bool, 0x05 u1, 0x08 i4, 0x09 u4, 0x0E string,
// 0x11 value type (an enum here; its underlying type is assumed to be int32).
std::string readValue(Reader& r, uint8_t type) {
    switch (type) {
        case 0x02: return r.u8() ? "true" : "false";
        case 0x05: return std::to_string(r.u8());
        case 0x08: return std::to_string(int32_t(r.u32()));
        case 0x09: case 0x11: case 0x55: return std::to_string(r.u32());
        case 0x0E: return r.serString();
        default: throw std::runtime_error("value type " + hex32(type) + " not handled");
    }
}

std::string attributeArguments(const Metadata& md, const std::vector<uint8_t>& ctorSignature, const std::vector<uint8_t>& value) {
    Reader sig{ctorSignature};
    Reader v{value};
    std::string out;
    sig.u8();                                // calling convention: 0x20 = HASTHIS (an instance constructor)
    const uint32_t count = sig.compressed();
    sig.u8();                                // return type: 0x01 = void
    if (v.u16() != 0x0001) return "(bad prolog)";
    for (uint32_t i = 0; i < count; i++) {
        uint8_t type = sig.u8();
        if (type == 0x11 || type == 0x12) sig.compressed();   // VALUETYPE/CLASS is followed by a TypeDefOrRef token
        out += (out.empty() ? "" : ", ") + readValue(v, type);
    }
    const uint16_t named = v.u16();
    for (uint16_t i = 0; i < named; i++) {
        const uint8_t kind = v.u8();          // 0x53 field, 0x54 property
        const uint8_t type = v.u8();
        if (type == 0x55) v.serString();      // an enum names its type first
        std::string name = v.serString();
        name = name.substr(1, name.size() - 2);
        out += (out.empty() ? "" : ", ") + name + (kind == 0x54 ? " = " : " (field) = ") + readValue(v, type);
    }
    (void)md;
    return out;
}

// The type name behind a CustomAttribute's constructor (a MemberRef whose parent is a TypeRef).
std::string attributeName(const Metadata& md, uint32_t ctorCoded, std::vector<uint8_t>& ctorSignature) {
    auto [table, row] = md.decode(CustomAttributeType, ctorCoded);
    if (table != MemberRef) return "(constructor in " + std::string(table >= 0 ? TableNames[table] : "?") + ")";
    ctorSignature = md.blob(md.get(MemberRef, row, 2));
    auto [parentTable, parentRow] = md.decode(MemberRefParent, md.get(MemberRef, row, 0));
    if (parentTable != TypeRef) return "(?)";
    std::string name = md.string(md.get(TypeRef, parentRow, 1));
    const std::string suffix = "Attribute";
    if (name.size() > suffix.size() && name.compare(name.size() - suffix.size(), suffix.size(), suffix) == 0) name.resize(name.size() - suffix.size());
    return name;
}

// ---------------------------------------------------------------------------------------------------------
// The report.
// ---------------------------------------------------------------------------------------------------------

void report(const std::string& path) {
    const PeImage pe = readPe(path);
    const Bytes& f = pe.file;
    std::printf("== %s (%zu bytes)\n\n", path.c_str(), f.data.size());

    std::printf("[PE container]\n");
    std::printf("  machine %s, %s, characteristics %s, subsystem %u\n", hex32(pe.machine).c_str(), pe.pe32Plus ? "PE32+" : "PE32",
                hex32(pe.characteristics).c_str(), pe.subsystem);
    for (const auto& s : pe.sections) {
        std::printf("  section %-6s RVA %-7s %5u bytes\n", s.name.c_str(), hex32(s.va).c_str(), s.vsize);
    }

    // The import table (directory 1): IL-only images import one function, mscoree.dll!_CorDllMain, and the entry
    // point is a 6-byte "jmp [import]" stub. Only Windows XP-era loaders used it; today the .NET host loads the
    // assembly itself and never runs this stub.
    if (pe.directories.size() > 1 && pe.directories[1].first != 0) {
        const size_t imports = pe.offsetOf(pe.directories[1].first);
        for (size_t d = imports; f.u32(d + 12) != 0; d += 20) {
            const std::string dll = f.cstr(pe.offsetOf(f.u32(d + 12)));
            const size_t thunks = pe.offsetOf(f.u32(d) != 0 ? f.u32(d) : f.u32(d + 16));
            for (size_t t = thunks;; t += pe.pe32Plus ? 8 : 4) {
                const uint64_t thunk = pe.pe32Plus ? f.u64(t) : f.u32(t);
                if (thunk == 0) break;
                std::printf("  imports %s!%s\n", dll.c_str(), f.cstr(pe.offsetOf(uint32_t(thunk)) + 2).c_str());
            }
        }
    }
    if (pe.entryPoint != 0) {
        const size_t stub = pe.offsetOf(pe.entryPoint);
        std::printf("  entry point %s: %s  (FF 25 = jmp dword ptr [import address])\n", hex32(pe.entryPoint).c_str(), hex(&f.data[stub], 6).c_str());
    }

    // The CLI header.
    const size_t cli = pe.offsetOf(pe.directories.at(14).first);
    const uint32_t flags = f.u32(cli + 16);
    std::printf("\n[CLI header]\n  runtime %u.%u, flags %s:%s%s%s%s\n", f.u16(cli + 4), f.u16(cli + 6), hex32(flags).c_str(),
                flags & 1 ? " ILONLY" : "", flags & 2 ? " 32BITREQUIRED" : "", flags & 8 ? " STRONGNAMESIGNED" : "",
                flags & 4 ? " ILLIBRARY (ReadyToRun native code)" : "");
    std::printf("  strong-name signature: %u bytes; ReadyToRun header: %u bytes\n", f.u32(cli + 36), f.u32(cli + 68));
    if ((flags & 1) && pe.machine == 0x14C && !(flags & 2)) {
        std::printf("  -> i386 + ILONLY without 32BITREQUIRED means \"AnyCPU\": pure IL that runs as 32- or 64-bit.\n");
    }

    const Metadata md = readMetadata(pe);
    std::printf("\n[Metadata %s]\n", md.version.c_str());
    for (int t = 0; t < TableCount; t++) {
        if (md.rows[t] != 0) std::printf("  %-16s %5u rows x %2d bytes\n", TableNames[t], md.rows[t], md.rowSize[t]);
    }

    // The Assembly row: this file's identity.
    if (md.rows[Assembly] != 0) {
        const auto key = md.blob(md.get(Assembly, 1, 6));
        std::printf("\n[Identity]\n  %s, Version=%u.%u.%u.%u, Culture=%s\n", md.string(md.get(Assembly, 1, 7)).c_str(),
                    md.get(Assembly, 1, 1), md.get(Assembly, 1, 2), md.get(Assembly, 1, 3), md.get(Assembly, 1, 4),
                    md.string(md.get(Assembly, 1, 8)).empty() ? "neutral" : md.string(md.get(Assembly, 1, 8)).c_str());
        const auto digest = sha1(key);
        std::string token;
        for (int i = 19; i >= 12; i--) token += hex(&digest[i], 1);
        std::printf("  public key (%zu bytes) %s -> PublicKeyToken=%s\n", key.size(), hex(key.data(), key.size()).c_str(), token.c_str());
        if (key.size() == 16) {
            std::printf("  -> a 16-byte key is the ECMA placeholder key, which the runtime treats as Microsoft's .NET Framework key.\n");
        }
    }

    // Assemblies this one depends on.
    std::printf("\n[References]\n");
    for (uint32_t r = 1; r <= md.rows[AssemblyRef]; r++) {
        const auto token = md.blob(md.get(AssemblyRef, r, 5));
        std::printf("  %s, Version=%u.%u.%u.%u, PublicKeyToken=%s\n", md.string(md.get(AssemblyRef, r, 6)).c_str(),
                    md.get(AssemblyRef, r, 0), md.get(AssemblyRef, r, 1), md.get(AssemblyRef, r, 2), md.get(AssemblyRef, r, 3),
                    hex(token.data(), token.size()).c_str());
    }

    // Types used from each reference: TypeRef rows whose ResolutionScope is an AssemblyRef. This answers
    // "what would break if that assembly were missing?"
    std::printf("\n[Types used from each reference]\n");
    for (uint32_t r = 1; r <= md.rows[AssemblyRef]; r++) {
        std::string used;
        uint32_t count = 0;
        for (uint32_t t = 1; t <= md.rows[TypeRef]; t++) {
            auto [scope, scopeRow] = md.decode(ResolutionScope, md.get(TypeRef, t, 0));
            if (scope != AssemblyRef || scopeRow != r) continue;
            if (++count <= 8) {
                const std::string ns = md.string(md.get(TypeRef, t, 2));
                used += (used.empty() ? "" : ", ") + ns + (ns.empty() ? "" : ".") + md.string(md.get(TypeRef, t, 1));
            }
        }
        std::printf("  %s: %u%s%s%s\n", md.string(md.get(AssemblyRef, r, 6)).c_str(), count, count ? " (" : "", used.c_str(),
                    count > 8 ? ", ...)" : count ? ")" : "");
    }

    // Types defined here. Row 1 is always <Module>, the holder of global functions and fields.
    std::printf("\n[Types defined here: %u]\n", md.rows[TypeDef]);
    for (uint32_t t = 1; t <= md.rows[TypeDef] && t <= 12; t++) {
        const uint32_t firstMethod = md.get(TypeDef, t, 5);
        const uint32_t nextMethod = t < md.rows[TypeDef] ? md.get(TypeDef, t + 1, 5) : md.rows[MethodDef] + 1;
        const std::string ns = md.string(md.get(TypeDef, t, 2));
        std::printf("  %s%s%s  (%u methods)\n", ns.c_str(), ns.empty() ? "" : ".", md.string(md.get(TypeDef, t, 1)).c_str(), nextMethod - firstMethod);
    }
    if (md.rows[TypeDef] > 12) std::printf("  ... and %u more\n", md.rows[TypeDef] - 12);

    // Type forwarders: ExportedType rows with the forwarder flag (0x00200000) whose Implementation is an
    // AssemblyRef. A reference to "System.Numerics.Vector3 in System.Numerics" is redirected by the loader to
    // the named assembly.
    if (md.rows[ExportedType] != 0) {
        std::printf("\n[Type forwarders: %u]\n", md.rows[ExportedType]);
        for (uint32_t e = 1; e <= md.rows[ExportedType]; e++) {
            const uint32_t typeFlags = md.get(ExportedType, e, 0);
            auto [table, row] = md.decode(Implementation, md.get(ExportedType, e, 4));
            const std::string target = table == AssemblyRef ? md.string(md.get(AssemblyRef, row, 6)) : TableNames[table];
            std::printf("  %s.%-12s -> %s%s\n", md.string(md.get(ExportedType, e, 3)).c_str(), md.string(md.get(ExportedType, e, 2)).c_str(),
                        target.c_str(), typeFlags & 0x00200000 ? "" : "  (exported, not forwarded)");
        }
    }

    // Assembly-level attributes (CustomAttribute rows whose Parent is the Assembly row).
    std::printf("\n[Assembly attributes]\n");
    for (uint32_t c = 1; c <= md.rows[CustomAttribute]; c++) {
        auto [parentTable, parentRow] = md.decode(HasCustomAttribute, md.get(CustomAttribute, c, 0));
        if (parentTable != Assembly) continue;
        (void)parentRow;
        std::vector<uint8_t> signature;
        const std::string name = attributeName(md, md.get(CustomAttribute, c, 1), signature);
        std::string arguments;
        try {
            arguments = attributeArguments(md, signature, md.blob(md.get(CustomAttribute, c, 2)));
        } catch (const std::exception& e) {
            arguments = std::string("? (") + e.what() + ")";
        }
        std::printf("  [%s(%s)]\n", name.c_str(), arguments.c_str());
    }

    // Method bodies: MethodDef rows with a non-zero RVA have IL code.
    uint32_t withCode = 0;
    for (uint32_t m = 1; m <= md.rows[MethodDef]; m++) if (md.get(MethodDef, m, 0) != 0) withCode++;
    std::printf("\n[Code]\n  %u methods, %u with IL bodies%s\n", md.rows[MethodDef], withCode,
                md.rows[MethodDef] == 0 ? ": this assembly has no executable code at all." : ".");
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::fprintf(stderr, "usage: %s <assembly.dll> [more.dll ...]\n", argv[0]);
        return 2;
    }
    int status = 0;
    for (int i = 1; i < argc; i++) {
        try {
            report(argv[i]);
        } catch (const std::exception& e) {
            std::fprintf(stderr, "%s: %s\n", argv[i], e.what());
            status = 1;
        }
        std::printf("\n");
    }
    return status;
}
