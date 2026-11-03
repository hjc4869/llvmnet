#!/usr/bin/env python3
"""Generate a native extension of the llvmnet portable ABI (ADR-15, docs/dev/extensions.md) from its description.

    ext-gen.py generate <extension.json> --output <dir> --llvmnet-version <version> [--source <name>=<dir> ...]
    ext-gen.py verify <output dir> <declarations.ll> ...
    ext-gen.py sources <extension.json>

generate reads the extension's description and the API registries it names (Khronos registry XML) and writes, under the
output directory:

    package/manifest.json, package/include/...   the extension package (docs/user/driver.md#link-inputs) without its
                                                 assembly, and package/share/licenses/ with the notices it names
    project/                                     the C# project of the extension assembly: Generated.cs and the project
                                                 file; build it with -p:LlvmNetRuntime=<LlvmNet.Runtime.dll>
    check/<group>.c, check/main.c                C files that include the extension's headers and declare every proxied
                                                 function again from the registry's own text, for llvmnet to compile
    signatures.json                              every stub's C types as LLVM declares them

Each entry point gets a CExport stub whose CIL signature is llvmnet's mapping of the C prototype, calling the host's
function through an unmanaged function pointer with the exact C types, bound on its first call. A table maps every name
to its stub, which dlsym and the API's lookup function (eglGetProcAddress) return. Callback parameters of the types the
description declares get pools of reverse thunks; entry points that return host function pointers fail when called.
Prototypes with types whose size differs between 64-bit hosts (long), long double, structures by value and variadic
prototypes are rejected. verify compares what llvmnet compiled from the check files (textual LLVM IR) with
signatures.json, so a type-mapping mistake fails the extension's build rather than a user's program. sources prints
the names of the pinned sources (scripts/sources.lock) the description uses. Output is deterministic.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import xml.etree.ElementTree as ET

# ---------------------------------------------------------------------------------------------------------------------
# C types. A resolved type is one of:
#   ('void',)  ('int', bits, signed, pointer_sized)  ('float',)  ('double',)  ('ptr',)  ('callback', typedef name)
# ---------------------------------------------------------------------------------------------------------------------

def integer(bits, signed, pointer_sized=False):
    return ('int', bits, signed, pointer_sized)


VOID = ('void',)
POINTER = ('ptr',)
# Types whose meaning is fixed on every 64-bit host the portable ABI runs on (LP64 Linux and macOS, LLP64 Windows).
BASE_TYPES = {
    'void': VOID,
    'char': integer(8, True), 'signed char': integer(8, True), 'unsigned char': integer(8, False),
    'short': integer(16, True), 'short int': integer(16, True), 'signed short': integer(16, True),
    'unsigned short': integer(16, False), 'unsigned short int': integer(16, False),
    'int': integer(32, True), 'signed': integer(32, True), 'signed int': integer(32, True),
    'unsigned': integer(32, False), 'unsigned int': integer(32, False),
    'long long': integer(64, True), 'long long int': integer(64, True), 'signed long long': integer(64, True),
    'unsigned long long': integer(64, False), 'unsigned long long int': integer(64, False),
    'float': ('float',), 'double': ('double',),
    'int8_t': integer(8, True), 'uint8_t': integer(8, False), 'int16_t': integer(16, True), 'uint16_t': integer(16, False),
    'int32_t': integer(32, True), 'uint32_t': integer(32, False), 'int64_t': integer(64, True), 'uint64_t': integer(64, False),
    'intptr_t': integer(64, True, True), 'uintptr_t': integer(64, False, True), 'ptrdiff_t': integer(64, True, True),
    'ssize_t': integer(64, True, True), 'size_t': integer(64, False, True),
    # KHR/khrplatform.h: fixed-width and pointer-sized by definition, whatever C spelling a host's copy uses.
    'khronos_int8_t': integer(8, True), 'khronos_uint8_t': integer(8, False), 'khronos_int16_t': integer(16, True),
    'khronos_uint16_t': integer(16, False), 'khronos_int32_t': integer(32, True), 'khronos_uint32_t': integer(32, False),
    'khronos_int64_t': integer(64, True), 'khronos_uint64_t': integer(64, False),
    'khronos_intptr_t': integer(64, True, True), 'khronos_uintptr_t': integer(64, False, True),
    'khronos_ssize_t': integer(64, True, True), 'khronos_usize_t': integer(64, False, True),
    'khronos_float_t': ('float',), 'khronos_utime_nanoseconds_t': integer(64, False),
    'khronos_stime_nanoseconds_t': integer(64, True), 'khronos_boolean_enum_t': integer(32, True),
}
REJECTED_TYPES = {
    'long': 'long is 64 bits in the portable ABI but 32 bits on Windows',
    'long int': 'long is 64 bits in the portable ABI but 32 bits on Windows',
    'signed long': 'long is 64 bits in the portable ABI but 32 bits on Windows',
    'unsigned long': 'unsigned long is 64 bits in the portable ABI but 32 bits on Windows',
    'unsigned long int': 'unsigned long is 64 bits in the portable ABI but 32 bits on Windows',
    'long double': 'long double is 80-bit extended precision in the portable ABI and differs between hosts',
    '_Bool': 'bool parameters are not supported yet', 'bool': 'bool parameters are not supported yet',
    '__int128': '128-bit integers are not passed the same way on every host',
    'unsigned __int128': '128-bit integers are not passed the same way on every host',
    'wchar_t': 'wchar_t is 32 bits in the portable ABI but 16 bits on Windows',
}
QUALIFIERS = re.compile(r'\b(const|volatile|restrict|__restrict|__restrict__)\b')
CSHARP_KEYWORDS = set('''abstract as base bool break byte case catch char checked class const continue decimal default delegate
do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is
lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed
short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using
virtual void volatile while'''.split())


class GenerationError(Exception):
    pass


def spaced(text):
    return ' '.join(text.replace('*', ' * ').replace('[', ' [ ').replace(']', ' ] ').split())


def csharp_name(name):
    return '@' + name if name in CSHARP_KEYWORDS else name


def cil_type(ctype):
    """llvmnet's CIL type for a C type (TypeSystem.Map): i1 to i32 int, i64 long, pointers nint."""
    kind = ctype[0]
    if kind == 'void':
        return 'void'
    if kind == 'int':
        return 'int' if ctype[1] <= 32 else 'long'
    if kind in ('float', 'double'):
        return kind
    return 'nint'


def native_type(ctype):
    """The exact C# type of the host's C type, so that the CLR passes it as the host's calling convention requires."""
    kind = ctype[0]
    if kind == 'int':
        bits, signed, pointer_sized = ctype[1:]
        if pointer_sized:
            return 'nint' if signed else 'nuint'
        return {8: ('sbyte', 'byte'), 16: ('short', 'ushort'), 32: ('int', 'uint'), 64: ('long', 'ulong')}[bits][0 if signed else 1]
    return cil_type(ctype)


def llvm_type(ctype):
    """How Clang declares the C type for the portable target: the LLVM type and the extension attribute of a narrow integer."""
    kind = ctype[0]
    if kind == 'int':
        bits, signed = ctype[1], ctype[2]
        return f'i{bits}' + ((' signext' if signed else ' zeroext') if bits < 32 else '')
    if kind in ('ptr', 'callback'):
        return 'ptr'
    return kind


def to_native(ctype, value):
    """Converts a stub's CIL argument to the host's C type."""
    managed, native = cil_type(ctype), native_type(ctype)
    return value if managed == native else f'({native}){value}'


def to_managed(ctype, value):
    """Converts a host's C value to llvmnet's CIL form: narrow integers are kept zero-extended (docs/dev/architecture.md)."""
    managed, native = cil_type(ctype), native_type(ctype)
    if ctype[0] == 'int' and ctype[1] < 32:
        unsigned = {8: 'byte', 16: 'ushort'}[ctype[1]]
        return f'(int)({unsigned}){value}' if native != unsigned else f'(int){value}'
    return value if managed == native else f'({managed}){value}'


# ---------------------------------------------------------------------------------------------------------------------
# Registries
# ---------------------------------------------------------------------------------------------------------------------

def text_without(element, skip='name'):
    """The C text of a <proto>, <param> or <type> element without its <name> child's text."""
    parts = [element.text or '']
    for child in element:
        if child.tag != skip:
            parts.append(''.join(child.itertext()) if child.tag != 'apientry' else '')
        parts.append(child.tail or '')
    return ''.join(parts)


def split_parameters(text):
    """'GLenum source, const GLchar *message' -> [('source', 'GLenum'), ('message', 'const GLchar *')]."""
    text = text.strip()
    if text in ('', 'void'):
        return []
    result = []
    for index, part in enumerate(text.split(',')):
        part = part.strip()
        if part == '...':
            raise GenerationError('variadic callback types are not supported')
        match = re.match(r'^(.*?[\s*])([A-Za-z_]\w*)\s*(\[[^\]]*\])?$', part)
        words = set(spaced(QUALIFIERS.sub(' ', part)).split())
        if match and match.group(2) not in ('int', 'char', 'short', 'long', 'float', 'double', 'void', 'signed', 'unsigned') and len(words) > 1:
            result.append((match.group(2), match.group(1) + (match.group(3) or '')))
        else:
            result.append((f'p{index}', part))
    return result


class Callback:
    def __init__(self, name, ret, params):
        self.name, self.ret, self.params = name, ret, params


class Command:
    def __init__(self, name, ret_text, params, registry):
        self.name, self.ret_text, self.params, self.registry = name, ret_text, params, registry
        self.ret = None
        self.types = []
        self.problem = None


class Registry:
    """One Khronos registry: its types, commands and which commands each API selects."""

    def __init__(self, spec, path, overrides):
        self.spec = spec
        self.path = path
        self.file = os.path.basename(path)
        with open(path, 'rb') as stream:
            data = stream.read()
        self.sha256 = hashlib.sha256(data).hexdigest()
        self.root = ET.fromstring(data)
        self.overrides = overrides
        self.typedefs = {}     # name -> C text of the aliased type
        self.callbacks = {}    # name -> (return text, [(param name, text)])
        self.structs = set()
        self.opaque = {}       # name -> why it cannot be resolved (#ifdef blocks, platform headers)
        self.type_elements = []
        for element in self.root.find('types'):
            if element.tag != 'type':
                continue
            self.type_elements.append(element)
            self._read_type(element)
        self.commands = {}
        for element in self.root.find('commands').findall('command'):
            proto = element.find('proto')
            name = proto.find('name').text
            params = [(param.find('name').text, text_without(param)) for param in element.findall('param')]
            self.commands[name] = Command(name, text_without(proto), params, self)

    def _read_type(self, element):
        name_element = element.find('name')
        name = name_element.text if name_element is not None else element.get('name')
        text = ''.join(element.itertext()) if name_element is None else text_without(element)
        full = ' '.join(text.split())
        if name is None:
            return
        if name_element is None:
            if full.startswith('#include') or not full:
                if not full:
                    self.opaque.setdefault(name, 'defined by a platform header')
                return
            self.opaque[name] = 'its definition depends on the platform (' + full.split('\n')[0][:40] + ')'
            return
        if full.startswith('#define'):
            return
        if full.startswith('struct') and not full.startswith('typedef'):
            self.structs.add(name)
            return
        callback = re.match(r'^typedef (.+?)\(\s*\*\s*\)\s*\((.*)\)\s*;$', full)
        if callback:
            self.callbacks[name] = (callback.group(1).strip(), split_parameters(callback.group(2)))
            return
        alias = re.match(r'^typedef (.+?)\s*;$', full)
        if alias:
            self.typedefs[name] = alias.group(1).strip()

    def selected(self, apis):
        """The commands the APIs' features and extensions require, in registry order."""
        apis = set(apis)
        names = set()

        def take(requirements):
            for require in requirements:
                if require.tag != 'require' or require.get('api') and require.get('api') not in apis:
                    continue
                names.update(command.get('name') for command in require.findall('command'))

        for feature in self.root.findall('feature'):
            if feature.get('api') in apis:
                take(feature)
        extensions = self.root.find('extensions')
        for extension in (extensions if extensions is not None else []):
            supported = set((extension.get('supported') or '').split('|'))
            if 'glcore' in supported:
                supported.add('gl')
            if supported & apis:
                take(extension)
        return [name for name in self.commands if name in names]


class Model:
    """Resolves every selected command's C types against the registries' typedefs and the description's overrides."""

    def __init__(self, description, registries):
        self.description = description
        self.registries = registries
        self.overrides = description.get('types', {})
        self.excluded_types = set(description.get('exclude-types', []))
        self.declared_callbacks = description.get('callbacks', {})
        self.callbacks = {}

    def lookup(self, name):
        for registry in self.registries:
            if name in registry.typedefs:
                return ('typedef', registry.typedefs[name])
            if name in registry.callbacks:
                return ('callback', registry.callbacks[name])
            if name in registry.structs:
                return ('struct', None)
        for registry in self.registries:
            if name in registry.opaque:
                return ('opaque', registry.opaque[name])
        return None

    def resolve(self, text, seen=()):
        stripped = spaced(QUALIFIERS.sub(' ', text))
        if '*' in stripped or '[' in stripped:
            return POINTER
        if stripped.startswith('struct '):
            raise GenerationError(f'{stripped} is passed by value; structures by value are not supported yet (NX-07)')
        if stripped in self.excluded_types:
            raise ExcludedType(stripped)
        if stripped in self.overrides:
            return self.resolve(self.overrides[stripped], seen + (stripped,))
        if stripped in REJECTED_TYPES:
            raise GenerationError(f'{stripped}: {REJECTED_TYPES[stripped]}')
        if stripped in BASE_TYPES:
            return BASE_TYPES[stripped]
        if stripped in seen:
            raise GenerationError(f'the typedef {stripped} refers to itself')
        found = self.lookup(stripped)
        if found is None:
            raise GenerationError(f'unknown C type {stripped!r} (add it to "types" in the extension description)')
        kind, value = found
        if kind == 'typedef':
            return self.resolve(value, seen + (stripped,))
        if kind == 'callback':
            self.callback(stripped, value)
            return ('callback', stripped)
        if kind == 'struct':
            raise GenerationError(f'struct {stripped} is passed by value; structures by value are not supported yet (NX-07)')
        raise GenerationError(f'{stripped} is {value}; give it a meaning in "types" or exclude it with "exclude-types"')

    def callback(self, name, signature):
        if name in self.callbacks:
            return self.callbacks[name]
        ret_text, params = signature
        try:
            ret = self.resolve(ret_text)
            types = [self.resolve(text) for _, text in params]
        except GenerationError as error:
            self.callbacks[name] = Callback(name, None, [])
            self.callbacks[name].problem = str(error)
            return self.callbacks[name]
        if ret[0] == 'callback' or any(t[0] == 'callback' for t in types):
            callback = Callback(name, None, [])
            callback.problem = 'it takes or returns another function pointer'
        else:
            callback = Callback(name, ret, [(param_name, t) for (param_name, _), t in zip(params, types)])
            callback.problem = None
        self.callbacks[name] = callback
        return callback


class ExcludedType(Exception):
    pass


# ---------------------------------------------------------------------------------------------------------------------
# The description
# ---------------------------------------------------------------------------------------------------------------------

NAME_PATTERN = re.compile(r'^[a-z][a-z0-9]*(-[a-z0-9]+)*$')


def pascal(name):
    return ''.join(part[0].upper() + part[1:] for part in name.split('-') if part)


def load_description(path):
    with open(path, encoding='utf-8') as stream:
        description = json.load(stream)
    for key in ('name', 'version', 'registries', 'libraries'):
        if key not in description:
            raise GenerationError(f'{path}: "{key}" is required')
    if not NAME_PATTERN.match(description['name']):
        raise GenerationError(f'{path}: the name {description["name"]!r} must be lowercase letters, digits and dashes')
    names = [library['name'] for library in description['libraries']]
    if len(set(names)) != len(names):
        raise GenerationError(f'{path}: two libraries have the same name')
    for library in description['libraries']:
        for key in ('linux', 'windows', 'macos', 'dlopen'):
            if not isinstance(library.get(key, []), list):
                raise GenerationError(f'{path}: library {library["name"]}: "{key}" must be a list')
        for dependency in library.get('preload', []):
            if dependency not in names or names.index(dependency) >= names.index(library['name']):
                raise GenerationError(f'{path}: library {library["name"]} preloads {dependency}, which must be listed before it')
    for registry in description['registries']:
        if registry.get('library') not in names:
            raise GenerationError(f'{path}: registry {registry.get("path")} names no library of "libraries"')
    if not any(library.get('dlopen') for library in description['libraries']):
        raise GenerationError(f'{path}: at least one library needs "dlopen" names, the names programs open it by')
    lookup = description.get('lookup')
    if lookup is not None and set(lookup) - {'command', 'library'}:
        raise GenerationError(f'{path}: "lookup" has "command" and optionally "library"')
    return description


def located(spec, base, sources):
    """'<source>:<relative path>' in a pinned source, or a path relative to the description's directory."""
    if ':' in spec and not os.path.isabs(spec):
        source, relative = spec.split(':', 1)
        if source not in sources:
            raise GenerationError(f'{spec}: no --source {source}=<dir> was given')
        return os.path.join(sources[source], relative)
    return os.path.join(base, spec)


# ---------------------------------------------------------------------------------------------------------------------
# C# emission
# ---------------------------------------------------------------------------------------------------------------------

def literal(text):
    return json.dumps(text)


def array(items):
    return '[' + ', '.join(literal(item) for item in items) + ']'


class Emitter:
    def __init__(self, description, model, registries, llvmnet_version):
        self.d = description
        self.model = model
        self.registries = registries
        self.version = llvmnet_version
        self.name = description['name']
        self.type_name = pascal(self.name) + 'Extension'
        self.namespace = 'LlvmNet.Ext.' + pascal(self.name)
        self.libraries = description['libraries']
        self.lookup = description.get('lookup')
        self.defaults = description.get('defaults', {})
        self.required = description.get('required', [])
        self.excluded = set(description.get('exclude', []))
        self.pools = description.get('callbacks', {})
        self.non_blocking = set(description.get('non-blocking', []))
        self.commands = []          # (Command, library name), proxied
        self.unsupported = []       # (Command, library name, reason): stubs that fail
        self.skipped = []           # (name, reason): no stub at all
        self.thunked = set()

    def select(self):
        seen = set()
        for spec, registry in self.registries:
            for name in registry.selected(spec['apis']):
                if name in seen:
                    continue
                seen.add(name)
                command = registry.commands[name]
                if name in self.excluded:
                    self.skipped.append((name, 'excluded by the description'))
                    continue
                try:
                    command.ret = self.model.resolve(command.ret_text)
                    command.types = [self.model.resolve(text) for _, text in command.params]
                except ExcludedType as excluded:
                    self.skipped.append((name, f'it uses {excluded.args[0]}, which the description excludes'))
                    continue
                except GenerationError as error:
                    raise GenerationError(f'{name} in {registry.file}: {error}')
                library = spec['library']
                if self.lookup and name == self.lookup['command']:
                    if [cil_type(t) for t in command.types] != ['nint'] or command.ret[0] not in ('ptr', 'callback'):
                        raise GenerationError(f'the lookup function {name} must take a name and return a function pointer')
                    self.commands.append((command, library))
                    continue
                if command.ret[0] == 'callback':
                    self.unsupported.append((command, library, f'it returns a host function pointer ({command.ret[1]}), which a portable program cannot call'))
                    continue
                self.commands.append((command, library))
                for t in command.types:
                    if t[0] == 'callback' and t[1] in self.pools:
                        callback = self.model.callbacks[t[1]]
                        if callback.problem:
                            raise GenerationError(f'the callback type {t[1]} cannot have reverse thunks: {callback.problem}')
                        self.thunked.add(t[1])
        for name in list(self.defaults) + self.required + sorted(self.non_blocking):
            if not any(command.name == name for command, _ in self.commands):
                raise GenerationError(f'"{name}" in "defaults", "required" or "non-blocking" is not a proxied entry point')
        for command, _ in self.commands:
            if command.name in self.non_blocking and any(t[0] == 'callback' for t in command.types):
                raise GenerationError(f'{command.name} takes a callback, so it cannot be "non-blocking"')
        for name in self.pools:
            if name not in self.model.callbacks:
                raise GenerationError(f'"callbacks" names {name}, which no proxied entry point takes')

    def library_field(self, name):
        return 'Library' + re.sub(r'\W', '_', name)

    def emit(self):
        out = []
        w = out.append
        sources = ', '.join(f'{registry.file} sha256:{registry.sha256[:16]}' for _, registry in self.registries)
        w('// <auto-generated/>')
        w(f'// The {self.name} native extension, generated by scripts/ext-gen.py from its description and {sources}.')
        w('// Do not edit: change the description or the generator. docs/dev/extensions.md describes the output.')
        w('#nullable enable')
        w('using System.Collections.Generic;')
        w('using System.Runtime.CompilerServices;')
        w('using System.Runtime.InteropServices;')
        w('using LlvmNet.Runtime;')
        w('')
        w('[assembly: System.Reflection.AssemblyMetadata("llvmnet.abi", "dotnet64-v2")]')
        w(f'[assembly: System.Reflection.AssemblyMetadata("llvmnet.version", {literal(self.version)})]')
        w(f'[assembly: System.Reflection.AssemblyMetadata("llvmnet.extension", {literal(self.name + "/" + self.d["version"])})]')
        w(f'[assembly: System.Reflection.AssemblyMetadata("llvmnet.extension.sources", {literal(sources)})]')
        w('')
        w(f'namespace {self.namespace};')
        w('')
        self.emit_extension(w)
        w('')
        self.emit_stubs(w)
        w('')
        self.emit_table(w)
        if self.non_blocking:
            w('')
            self.emit_non_blocking(w)
        if self.thunked:
            w('')
            self.emit_callbacks(w)
        return '\n'.join(out) + '\n'

    def emit_extension(self, w):
        served = [name for library in self.libraries for name in library.get('dlopen', [])]
        w(f'/// <summary>The {self.name} native extension: its host libraries, how its stubs bind, and what dlopen serves.</summary>')
        w(f'public sealed unsafe class {self.type_name}() : NativeExtension({literal(self.name)}{"".join(", " + literal(s) for s in served)})')
        w('{')
        for library in self.libraries:
            preload = ''.join(', ' + self.library_field(p) for p in library.get('preload', []))
            w(f'    internal static readonly HostLibrary {self.library_field(library["name"])} = new({literal(self.name)}, {literal(library["name"])}, '
              f'{array(library.get("linux", []))}, {array(library.get("windows", []))}, {array(library.get("macos", []))}{preload});')
        fields = ', '.join(self.library_field(library['name']) for library in self.libraries)
        w(f'    internal static readonly HostEntryPoints Host = new({literal(self.name)}, [{fields}]{", Lookup" if self.lookup else ""});')
        w('')
        if self.lookup:
            command = self.lookup['command']
            library = self.lookup.get('library') or next(l for c, l in self.commands if c.name == command)
            w('    private static nint lookupFunction;')
            w('')
            w(f'    /// <summary>The host\'s {command}: the address of a function its libraries do not export, or 0.</summary>')
            w('    private static nint Lookup(string name)')
            w('    {')
            w('        nint function = lookupFunction;')
            w(f'        if (function == 0 && (function = lookupFunction = {self.library_field(library)}.Export({literal(command)})) == 0) return 0;')
            w('        byte[] text = System.Text.Encoding.UTF8.GetBytes(name + "\\0");')
            w('        fixed (byte* pointer = text) return ((delegate* unmanaged<nint, nint>)function)((nint)pointer);')
            w('    }')
            w('')
            w(f'    /// <summary>{command} for portable code: the stub of a function the host provides, else 0, as natively.</summary>')
            w('    internal static nint ProcAddress(nint name)')
            w('    {')
            w('        if (name == 0) return 0;')
            w('        string text = Marshal.PtrToStringUTF8(name)!;')
            w('        return Commands.Table.TryGetValue(text, out Commands.Entry entry) && Host.Find(entry.Library, text) != 0 ? entry.Stub : 0;')
            w('    }')
            w('')
        w('    private static HostLibrary Serving(string library) => library switch')
        w('    {')
        for library in self.libraries:
            if library.get('dlopen'):
                w(f'        {" or ".join(literal(n) for n in library["dlopen"])} => {self.library_field(library["name"])},')
        w(f'        _ => throw new System.ArgumentException("The native extension {self.name} does not serve " + library + ".")')
        w('    };')
        w('')
        w('    public override string? Open(string library) => Serving(library).Problem;')
        w('')
        w('    /// <summary>')
        w('    /// dlsym: the stub of a function that the host\'s own lookup in the opened library finds (on Linux and macOS it')
        w('    /// searches the library\'s dependencies too, on Windows only the library), or 0, as natively.')
        w('    /// </summary>')
        w('    public override nint Resolve(string library, string symbol) =>')
        w('        Commands.Table.TryGetValue(symbol, out Commands.Entry entry) && Serving(library).Export(symbol) != 0 ? entry.Stub : 0;')
        w('')
        required = ', '.join(f'({self.library_field(self.library_of(name))}, {literal(name)})' for name in self.required)
        w(f'    public override string? Unavailable() => Host.Unavailable({required});')
        w('')
        w('    public override string Report() => Host.Report();')
        w('')
        w('    internal static void Unsupported(string message) => Fail(message);')
        w('}')

    def library_of(self, name):
        return next(library for command, library in self.commands if command.name == name)

    def emit_stubs(self, w):
        w(f'/// <summary>One CExport stub per entry point of the {self.name} extension.</summary>')
        w('public static unsafe class Api')
        w('{')
        for command, library in self.commands:
            self.emit_stub(w, command, library)
        for command, library, reason in self.unsupported:
            params = ', '.join(f'{cil_type(t)} {csharp_name(n)}' for (n, _), t in zip(command.params, command.types))
            w(f'    [CExport({literal(command.name)})]')
            w(f'    public static {cil_type(command.ret)} {command.name}({params})')
            w('    {')
            w(f'        {self.type_name}.Unsupported({literal(command.name + ": " + reason + "; the native extension " + self.name + " does not provide it")});')
            if command.ret[0] != 'void':
                w('        return 0;')
            w('    }')
            w('')
        w('}')

    def emit_stub(self, w, command, library):
        names = [csharp_name(n) for n, _ in command.params]
        params = ', '.join(f'{cil_type(t)} {n}' for n, t in zip(names, command.types))
        ret = cil_type(command.ret)
        w(f'    [CExport({literal(command.name)})]')
        if self.lookup and command.name == self.lookup['command']:
            w(f'    public static nint {command.name}({params}) => {self.type_name}.ProcAddress({names[0]});')
            w('')
            return
        slot = f'__{command.name}'
        field = self.library_field(library)
        non_blocking = command.name in self.non_blocking
        w(f'    public static {ret} {command.name}({params})')
        w('    {')
        args = []
        for name, t in zip(names, command.types):
            if t[0] == 'callback':
                if t[1] in self.thunked:
                    args.append(f'Callbacks.{t[1]}.Thunk({name})')
                else:
                    args.append(name)
            else:
                args.append(to_native(t, name))
        signature = ', '.join([native_type(t) for t in command.types] + [native_type(command.ret)])
        if non_blocking:
            # With LLVMNET_EXT_<NAME>_NON_BLOCKING=1, an entry point the description marks non-blocking skips the GC
            # transition, which is safe only when the host's implementation takes no lock and never calls back. The
            # address sits in a slot of its own, filled at binding only then, and the ordinary call is a method of its
            # own: the JIT sets up a transition frame in the prologue of any method with an ordinary unmanaged call,
            # and the NativeAOT compiler can merge two calls of one pointer that differ only in the transition.
            fast = f'((delegate* unmanaged[SuppressGCTransition]<{signature}>)__g)({", ".join(args)})'
            forward = ', '.join(names)
            w(f'        nint __g = {slot}_non_blocking;')
            w(f'        if (__g != 0) {"{ " + fast + "; return; }" if ret == "void" else "return " + to_managed(command.ret, fast) + ";"}')
            w(f'        {"" if ret == "void" else "return "}{command.name}_transition({forward});')
            w('    }')
            w(f'    private static nint {slot}_non_blocking;')
            w('')
            w('    [MethodImpl(MethodImplOptions.NoInlining)]')
            w(f'    private static {ret} {command.name}_transition({params})')
            w('    {')
        w(f'        nint __f = {slot};')
        bind_suffix = f' if (NonBlocking.Enabled) {slot}_non_blocking = __f;' if non_blocking else ''
        if command.name in self.defaults:
            default = self.defaults[command.name]
            value = '' if ret == 'void' else f' {default}'
            w(f'        if (__f == 0 && (__f = {self.type_name}.Host.TryBind(ref {slot}, {self.type_name}.{field}, {literal(command.name)})) == 0) return{value};')
            if non_blocking:
                w(f'       {bind_suffix}')
        else:
            if non_blocking:
                w(f'        if (__f == 0) {{ __f = {self.type_name}.Host.Bind(ref {slot}, {self.type_name}.{field}, {literal(command.name)});{bind_suffix} }}')
            else:
                w(f'        if (__f == 0) __f = {self.type_name}.Host.Bind(ref {slot}, {self.type_name}.{field}, {literal(command.name)});')
        for name, t in zip(names, command.types):
            if t[0] == 'callback' and t[1] not in self.thunked:
                w(f'        if ({name} != 0) {self.type_name}.Unsupported({literal(command.name + ": the native extension " + self.name + " has no reverse thunks for " + t[1] + " callbacks, so it can pass only a null one")});')
        call = f'((delegate* unmanaged<{signature}>)__f)({", ".join(args)})'
        if ret == 'void':
            w(f'        {call};')
        else:
            w(f'        return {to_managed(command.ret, call)};')
        w('    }')
        w(f'    private static nint {slot};')
        w('')

    def emit_non_blocking(self, w):
        variable = f'LLVMNET_EXT_{self.name.upper().replace("-", "_")}_NON_BLOCKING'
        w(f'/// <summary>Whether the non-blocking entry points skip the GC transition: {variable}=1 (docs/user/extensions.md).</summary>')
        w('internal static class NonBlocking')
        w('{')
        w(f'    internal static readonly bool Enabled = System.Environment.GetEnvironmentVariable({literal(variable)}) == "1";')
        w('}')

    def emit_table(self, w):
        w('/// <summary>Every proxied entry point by name, with its stub and the host library it belongs to.</summary>')
        w('internal static unsafe class Commands')
        w('{')
        w('    internal readonly record struct Entry(nint Stub, HostLibrary Library);')
        w('')
        w('    private static Dictionary<string, Entry>? table;')
        w('')
        w('    internal static Dictionary<string, Entry> Table => table ??= Build();')
        w('')
        chunks = [self.commands[i:i + 400] for i in range(0, len(self.commands), 400)]
        w('    private static Dictionary<string, Entry> Build()')
        w('    {')
        w(f'        var table = new Dictionary<string, Entry>({len(self.commands)}, System.StringComparer.Ordinal);')
        for index in range(len(chunks)):
            w(f'        Add{index}(table);')
        w('        return table;')
        w('    }')
        for index, chunk in enumerate(chunks):
            w('')
            w(f'    private static void Add{index}(Dictionary<string, Entry> table)')
            w('    {')
            for command, library in chunk:
                types = ', '.join([cil_type(t) for t in command.types] + [cil_type(command.ret)])
                w(f'        table.Add({literal(command.name)}, new((nint)(delegate*<{types}>)&Api.{command.name}, {self.type_name}.{self.library_field(library)}));')
            w('    }')
        w('}')

    def emit_callbacks(self, w):
        w('/// <summary>Reverse thunks: native-callable entry points that call the portable functions passed as callbacks.</summary>')
        w('internal static unsafe class Callbacks')
        w('{')
        for name in sorted(self.thunked):
            callback = self.model.callbacks[name]
            size = int(self.pools[name])
            names = [csharp_name(n) for n, _ in callback.params]
            native_params = ', '.join(f'{native_type(t)} {n}' for (_, t), n in zip(callback.params, names))
            native_signature = ', '.join([native_type(t) for _, t in callback.params] + [native_type(callback.ret)])
            managed_signature = ', '.join([cil_type(t) for _, t in callback.params] + [cil_type(callback.ret)])
            forwarded = ', '.join(names)
            ret = native_type(callback.ret)
            w(f'    internal static class {name}')
            w('    {')
            w(f'        private static readonly nint[] targets = new nint[{size}];')
            thunks = ', '.join(f'(nint)(delegate* unmanaged<{native_signature}>)&T{i}' for i in range(size))
            w(f'        private static readonly nint[] thunks = [{thunks}];')
            w('')
            w(f'        internal static nint Thunk(nint target) => target == 0 ? 0 : thunks[NativeCallbacks.Slot(targets, target, {literal(self.name)}, {literal(name)})];')
            for i in range(size):
                w('')
                w('        [UnmanagedCallersOnly]')
                call = f'Call(targets[{i}]{"".join(", " + n for n in names)})'
                w(f'        private static {ret} T{i}({native_params}) => {call};')
            w('')
            call_params = ''.join(f', {native_type(t)} {n}' for (_, t), n in zip(callback.params, names))
            args = ', '.join(to_managed(t, n) for (_, t), n in zip(callback.params, names))
            invoke = f'((delegate*<{managed_signature}>)target)({args})'
            body = invoke if callback.ret[0] == 'void' else to_native(callback.ret, invoke)
            w(f'        private static {ret} Call(nint target{call_params}) => {body};')
            w('    }')
        w('}')

    def project(self, assembly):
        return f'''<Project Sdk="Microsoft.NET.Sdk">
  <!-- Generated by scripts/ext-gen.py. Build with -p:LlvmNetRuntime=(path of LlvmNet.Runtime.dll). -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>{assembly}</AssemblyName>
    <RootNamespace>{self.namespace}</RootNamespace>
    <Version>{self.d["version"]}</Version>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <IsAotCompatible>true</IsAotCompatible>
    <Deterministic>true</Deterministic>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <NoWarn>$(NoWarn);CS8981</NoWarn>
    <DebugType>none</DebugType>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="LlvmNet.Runtime" HintPath="$(LlvmNetRuntime)" Private="false" />
  </ItemGroup>
</Project>
'''


# ---------------------------------------------------------------------------------------------------------------------
# Check files and verification
# ---------------------------------------------------------------------------------------------------------------------

def check_file(description, group, emitter, registries):
    """A C file that includes the group's headers, defines the registry types they may not define, and declares every
    proxied function of the group's APIs from the registry's text: a declaration that contradicts a header's is a
    compile error, and the address table makes llvmnet resolve each one against the extension assembly."""
    apis = set(group['apis'])
    lines = [f'/* Generated by scripts/ext-gen.py: the {group["name"]} check of the {description["name"]} native extension. */']
    lines += [f'#define {define} 1' for define in group.get('defines', [])]
    lines += [f'#include <{header}>' for header in group.get('includes', [])]
    lines.append('#ifndef LLVMNET_CHECK_APIENTRY')
    lines.append('#define LLVMNET_CHECK_APIENTRY')
    lines.append('#endif')
    names = []
    for spec, registry in registries:
        if not apis & set(spec['apis']):
            continue
        for element in registry.type_elements:
            text = ''.join(part if isinstance(part, str) else '' for part in element.itertext())
            if element.find('apientry') is not None:
                text = ''
                text += element.text or ''
                for child in element:
                    text += ('LLVMNET_CHECK_APIENTRY' if child.tag == 'apientry' else ''.join(child.itertext())) + (child.tail or '')
            stripped = text.strip()
            if not stripped or '{' in stripped:
                continue
            lines.append(stripped)
        selected = set(registry.selected(apis & set(spec['apis'])))
        for command, _ in emitter.commands + [(c, l) for c, l, _ in emitter.unsupported]:
            if command.registry is not registry or command.name not in selected:
                continue
            params = ', '.join(' '.join(f'{text} {name}'.split()) for name, text in command.params) or 'void'
            lines.append(f'{" ".join(command.ret_text.split())} {command.name}({params});')
            names.append(command.name)
    symbol = re.sub(r'\W', '_', group['name'])
    lines.append(f'__attribute__((used)) static void (*const llvmnet_check_{symbol}[])(void) = {{')
    lines += [f'    (void (*)(void)){name},' for name in names]
    lines.append('};')
    return '\n'.join(lines) + '\n', names


def signature_of(command):
    return [llvm_type(command.ret)] + [llvm_type(t) for t in command.types]


def generate(arguments):
    description_path = os.path.abspath(arguments.description)
    base = os.path.dirname(description_path)
    description = load_description(description_path)
    sources = dict(item.split('=', 1) for item in arguments.source or [])
    registries = []
    for spec in description['registries']:
        path = located(spec['path'], base, sources)
        registries.append((spec, Registry(spec, path, description.get('types', {}))))
    model = Model(description, [registry for _, registry in registries])
    emitter = Emitter(description, model, registries, arguments.llvmnet_version)
    emitter.select()
    output = os.path.abspath(arguments.output)
    if os.path.isdir(output):
        shutil.rmtree(output)
    package, project, check = (os.path.join(output, part) for part in ('package', 'project', 'check'))
    for directory in (package, project, check):
        os.makedirs(directory)
    assembly = 'LlvmNet.Ext.' + pascal(description['name'])
    write(os.path.join(project, 'Generated.cs'), emitter.emit())
    write(os.path.join(project, assembly + '.csproj'), emitter.project(assembly))
    # The generated project stands alone: no Directory.Build.props or .targets of a parent directory applies.
    write(os.path.join(project, 'Directory.Build.props'), '<Project />\n')
    write(os.path.join(project, 'Directory.Build.targets'), '<Project />\n')
    headers = []
    for entry in description.get('headers', []):
        root = located(entry['from'], base, sources)
        for header in entry['files']:
            source = os.path.join(root, header)
            if not os.path.isfile(source):
                raise GenerationError(f'the header {header} is missing from {root}')
            destination = os.path.join(package, 'include', header)
            if header in headers:
                raise GenerationError(f'the header {header} is listed twice')
            os.makedirs(os.path.dirname(destination), exist_ok=True)
            shutil.copyfile(source, destination)
            headers.append(header)
    for entry in description.get('licenses', []):
        source = located(entry['from'], base, sources)
        destination = os.path.join(package, 'share', 'licenses', description['name'], entry['as'])
        os.makedirs(os.path.dirname(destination), exist_ok=True)
        shutil.copyfile(source, destination)
    manifest = {
        'name': description['name'],
        'version': description['version'],
        'summary': description.get('summary', ''),
        'libraries': {system: [library[system][0] for library in description['libraries'] if library.get(system)]
                      for system in ('linux', 'windows', 'macos')},
        'link': description.get('link', []),
        'headers': [h for h in description.get('public-headers', headers)],
        'sources': {registry.file: registry.sha256 for _, registry in registries},
    }
    write(os.path.join(package, 'manifest.json'), json.dumps(manifest, indent=2) + '\n')
    covered = set()
    groups = description.get('checks') or [{'name': 'all', 'apis': sorted({api for spec, _ in registries for api in spec['apis']}),
                                            'includes': [h for h in headers if h.endswith('.h')]}]
    for group in groups:
        text, names = check_file(description, group, emitter, registries)
        write(os.path.join(check, group['name'] + '.c'), text)
        covered.update(names)
    missing = [command.name for command, _ in emitter.commands if command.name not in covered]
    if missing:
        raise GenerationError(f'no check group declares {", ".join(missing[:5])}{" and others" if len(missing) > 5 else ""}')
    write(os.path.join(check, 'main.c'), 'int main(void) { return 0; }\n')
    signatures = {command.name: signature_of(command) for command, _ in emitter.commands + [(c, l) for c, l, _ in emitter.unsupported]}
    write(os.path.join(output, 'signatures.json'), json.dumps(signatures, indent=1, sort_keys=True) + '\n')
    print(f'{description["name"]} {description["version"]}: {len(emitter.commands)} entry points, '
          f'{len(emitter.unsupported)} that fail when called, {len(emitter.skipped)} left out, '
          f'{len(emitter.thunked)} callback types with reverse thunks')
    for name, reason in emitter.skipped:
        print(f'  left out {name}: {reason}')
    for command, _, reason in emitter.unsupported:
        print(f'  fails when called: {command.name}: {reason}')


def write(path, text):
    with open(path, 'w', encoding='utf-8', newline='\n') as stream:
        stream.write(text)


DECLARATION = re.compile(r'^declare\s+(.*?)@"?([^"(\s]+)"?\((.*)\)')
LLVM_TYPES = re.compile(r'^(void|i\d+|ptr|float|double)$')


def parse_type(text):
    words = text.split()
    kinds = [word for word in words if LLVM_TYPES.match(word)]
    if len(kinds) != 1:
        raise GenerationError(f'cannot read the LLVM type of {text!r}')
    kind = kinds[0]
    if kind in ('i8', 'i16'):
        kind += ' signext' if 'signext' in words else ' zeroext' if 'zeroext' in words else ''
    return kind


def verify(arguments):
    with open(os.path.join(arguments.output, 'signatures.json'), encoding='utf-8') as stream:
        expected = json.load(stream)
    declared = {}
    for path in arguments.ir:
        with open(path, encoding='utf-8') as stream:
            for line in stream:
                match = DECLARATION.match(line)
                if not match or match.group(2) not in expected:
                    continue
                params = [part for part in match.group(3).split(',') if part.strip()]
                signature = [parse_type(match.group(1))] + [parse_type(part) for part in params]
                if match.group(2) in declared and declared[match.group(2)] != signature:
                    raise GenerationError(f'{match.group(2)} is declared differently in two check files')
                declared[match.group(2)] = signature
    problems = []
    for name, signature in sorted(expected.items()):
        if name not in declared:
            problems.append(f'{name}: no check file declares it')
        elif declared[name] != signature:
            problems.append(f'{name}: the stub calls ({", ".join(signature[1:])}) -> {signature[0]}, '
                            f'but llvmnet compiles ({", ".join(declared[name][1:])}) -> {declared[name][0]}')
    if problems:
        raise GenerationError('the stubs do not match the C prototypes:\n  ' + '\n  '.join(problems[:40]))
    print(f'verified {len(expected)} stubs against the C prototypes llvmnet compiled')


def list_sources(arguments):
    description = load_description(arguments.description)
    names = set()
    for spec in description['registries']:
        if ':' in spec['path']:
            names.add(spec['path'].split(':', 1)[0])
    for entry in description.get('headers', []) + description.get('licenses', []):
        if ':' in entry['from']:
            names.add(entry['from'].split(':', 1)[0])
    print(' '.join(sorted(names)))


def main():
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    commands = parser.add_subparsers(dest='command', required=True)
    generate_parser = commands.add_parser('generate')
    generate_parser.add_argument('description')
    generate_parser.add_argument('--output', required=True)
    generate_parser.add_argument('--llvmnet-version', required=True)
    generate_parser.add_argument('--source', action='append', metavar='NAME=DIR')
    verify_parser = commands.add_parser('verify')
    verify_parser.add_argument('output')
    verify_parser.add_argument('ir', nargs='+')
    sources_parser = commands.add_parser('sources')
    sources_parser.add_argument('description')
    arguments = parser.parse_args()
    try:
        {'generate': generate, 'verify': verify, 'sources': list_sources}[arguments.command](arguments)
    except (GenerationError, OSError, json.JSONDecodeError, ET.ParseError) as error:
        print(f'ext-gen.py: {error}', file=sys.stderr)
        sys.exit(1)


if __name__ == '__main__':
    main()
