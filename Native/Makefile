# Builds the native PTY helper next to this Makefile in build/.
# macOS: universal dylib (arm64 + x86_64). Linux: shared object.
# Invoked automatically by AvaTerm.Pty.csproj before every build.

UNAME_S := $(shell uname -s)
CC      ?= cc
CFLAGS  += -O2 -Wall -Wextra -fPIC -fvisibility=hidden
OUT     := build

ifeq ($(UNAME_S),Darwin)
  LIB     := $(OUT)/libCatminal_pty.dylib
  LDFLAGS += -dynamiclib -arch arm64 -arch x86_64 -install_name @rpath/libCatminal_pty.dylib
  CFLAGS  += -arch arm64 -arch x86_64 -mmacosx-version-min=11.0
else
  LIB     := $(OUT)/libCatminal_pty.so
  LDFLAGS += -shared
  LDLIBS  += -lutil
endif

all: $(LIB)

$(LIB): Catminal_pty.c
	@mkdir -p $(OUT)
	$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $< $(LDLIBS)

clean:
	rm -rf $(OUT)

.PHONY: all clean
