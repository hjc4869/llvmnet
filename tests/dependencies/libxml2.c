#include <stdio.h>
#include <string.h>
#include <libxml/parser.h>
#include <libxml/tree.h>
int main(void)
{
    const char *document = "<?xml version=\"1.0\"?><MPD type=\"static\"><Period>video &amp; audio</Period></MPD>";
    xmlDocPtr parsed = xmlReadMemory(document, (int)strlen(document), "manifest.mpd", NULL, XML_PARSE_NONET);
    if (!parsed) return 1;
    xmlNodePtr root = xmlDocGetRootElement(parsed);
    if (!root || strcmp((const char *)root->name, "MPD")) return 2;
    xmlChar *type = xmlGetProp(root, (const xmlChar *)"type");
    xmlChar *content = xmlNodeGetContent(root);
    if (!type || !content || strcmp((const char *)type, "static") || strcmp((const char *)content, "video & audio")) return 3;
    printf("libxml2 %s %s\n", type, content);
    xmlFree(type);
    xmlFree(content);
    xmlFreeDoc(parsed);
    xmlCleanupParser();
    return 0;
}