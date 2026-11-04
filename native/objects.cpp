#include <llvm-c/Core.h>
#include <llvm/BinaryFormat/Magic.h>
#include <llvm/Object/Archive.h>
#include <llvm/Object/ObjectFile.h>
#include <llvm/Support/FileSystem.h>
#include <llvm/Support/MemoryBuffer.h>
#include <llvm/Support/Path.h>
#include <llvm/Support/raw_ostream.h>
#include <string>

namespace {

bool isNativeObject(llvm::file_magic magic)
{
    return magic == llvm::file_magic::elf_relocatable || magic == llvm::file_magic::coff_object ||
        magic == llvm::file_magic::macho_object;
}

// Appends "F\t<file>" and a line per global symbol of the object: "T\t<name>" for a function (a symbol in an executable
// section), "D\t<name>" for data and "U\t<name>" for an undefined symbol.
std::string describe(llvm::MemoryBufferRef buffer, const std::string &file, std::string &listing)
{
    auto object = llvm::object::ObjectFile::createObjectFile(buffer);
    if (!object)
        return file + ": " + llvm::toString(object.takeError());
    listing += "F\t" + file + "\n";
    for (const llvm::object::SymbolRef &symbol : (*object)->symbols()) {
        auto flags = symbol.getFlags();
        if (!flags)
            return file + ": " + llvm::toString(flags.takeError());
        auto name = symbol.getName();
        if (!name)
            return file + ": " + llvm::toString(name.takeError());
        if (name->empty() || (*flags & llvm::object::SymbolRef::SF_FormatSpecific))
            continue;
        if (*flags & llvm::object::SymbolRef::SF_Undefined) {
            listing += "U\t" + name->str() + "\n";
            continue;
        }
        if (!(*flags & llvm::object::SymbolRef::SF_Global))
            continue;
        auto section = symbol.getSection();
        if (!section)
            return file + ": " + llvm::toString(section.takeError());
        bool text = *section != (*object)->section_end() && (*section)->isText();
        listing += std::string(text ? "T\t" : "D\t") + name->str() + "\n";
    }
    return "";
}

} // namespace

// Native object files for system mode (SV-15): a project's assembled kernels, given as objects or as members of the
// project's archives beside its bitcode. For an ELF, COFF or Mach-O object `path`, or each such member of an archive
// (written into `directory` under a unique name, as one archive can hold two members of one name), the listing has the
// file and its global symbols (describe). Bitcode members and other inputs list nothing.
extern "C" int llvmnet_native_objects(const char *path, const char *directory, char **listing, char **message)
{
    auto buffer = llvm::MemoryBuffer::getFile(path);
    if (!buffer) {
        *message = LLVMCreateMessage((std::string(path) + ": " + buffer.getError().message()).c_str());
        return 1;
    }
    std::string text, problem;
    llvm::file_magic magic = llvm::identify_magic((*buffer)->getBuffer());
    if (isNativeObject(magic)) {
        problem = describe((*buffer)->getMemBufferRef(), path, text);
    } else if (magic == llvm::file_magic::archive) {
        auto archive = llvm::object::Archive::create((*buffer)->getMemBufferRef());
        if (!archive) {
            *message = LLVMCreateMessage(llvm::toString(archive.takeError()).c_str());
            return 1;
        }
        llvm::Error iteration = llvm::Error::success();
        unsigned index = 0;
        for (const auto &child : (*archive)->children(iteration)) {
            auto data = child.getMemoryBufferRef();
            if (!data) {
                problem = llvm::toString(data.takeError());
                break;
            }
            if (!isNativeObject(llvm::identify_magic(data->getBuffer())))
                continue;
            auto name = child.getName();
            llvm::SmallString<256> file(directory);
            llvm::sys::path::append(file, std::to_string(index++) + "-" + llvm::sys::path::filename(name ? *name : "member.o").str());
            std::error_code error;
            llvm::raw_fd_ostream stream(file, error, llvm::sys::fs::OF_None);
            if (error) {
                problem = file.str().str() + ": " + error.message();
                break;
            }
            stream << data->getBuffer();
            stream.close();
            problem = describe(*data, file.str().str(), text);
            if (!problem.empty())
                break;
        }
        if (iteration)
            problem = llvm::toString(std::move(iteration));
    }
    if (!problem.empty()) {
        *message = LLVMCreateMessage(problem.c_str());
        return 1;
    }
    *listing = LLVMCreateMessage(text.c_str());
    return 0;
}
