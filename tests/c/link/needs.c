/* In an archive: needs a symbol from another archive that is named before this one on the command line. */
extern int provided(void);
int needs(void) { return provided(); }
