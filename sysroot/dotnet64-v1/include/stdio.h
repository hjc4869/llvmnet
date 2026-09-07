#ifndef __LLVMNET_STDIO_H
#define __LLVMNET_STDIO_H
#include <stddef.h>
#include <stdarg.h>
#define EOF (-1)
#define SEEK_SET 0
#define SEEK_CUR 1
#define SEEK_END 2
#define _IOFBF 0
#define _IOLBF 1
#define _IONBF 2
#define BUFSIZ 8192
#define FILENAME_MAX 4096
#define FOPEN_MAX 256
typedef long fpos_t;
__LLVMNET_BEGIN_C
extern FILE *__llvmnet_stdin;
extern FILE *__llvmnet_stdout;
extern FILE *__llvmnet_stderr;
#define stdin __llvmnet_stdin
#define stdout __llvmnet_stdout
#define stderr __llvmnet_stderr
FILE *fopen(const char *, const char *);
FILE *fdopen(int, const char *);
int fclose(FILE *);
int fflush(FILE *);
int fileno(FILE *);
size_t fread(void *, size_t, size_t, FILE *);
size_t fwrite(const void *, size_t, size_t, FILE *);
int fseek(FILE *, long, int);
int fseeko(FILE *, off_t, int);
long ftell(FILE *);
off_t ftello(FILE *);
void rewind(FILE *);
int feof(FILE *);
int ferror(FILE *);
void clearerr(FILE *);
int setvbuf(FILE *, char *, int, size_t);
void setbuf(FILE *, char *);
int fgetc(FILE *);
int getc(FILE *);
int getchar(void);
int ungetc(int, FILE *);
int fputc(int, FILE *);
int putc(int, FILE *);
int putchar(int);
int fputs(const char *, FILE *);
int puts(const char *);
char *fgets(char *, int, FILE *);
int printf(const char *, ...);
int fprintf(FILE *, const char *, ...);
int sprintf(char *, const char *, ...);
int snprintf(char *, size_t, const char *, ...);
int vprintf(const char *, va_list);
int vfprintf(FILE *, const char *, va_list);
int vsprintf(char *, const char *, va_list);
int vsnprintf(char *, size_t, const char *, va_list);
int sscanf(const char *, const char *, ...);
int fscanf(FILE *, const char *, ...);
int scanf(const char *, ...);
int vsscanf(const char *, const char *, va_list);
int vfscanf(FILE *, const char *, va_list);
int vscanf(const char *, va_list);
int remove(const char *);
int rename(const char *, const char *);
void perror(const char *);
__LLVMNET_END_C
#endif