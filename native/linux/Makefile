# Builds libmsbpvdi.so against the SQL Server on Linux VDI client library.
# Run on the SQL Server machine (needs g++ and /opt/mssql/lib/libsqlvdi.so,
# which ships with SQL Server on Linux 2017+).
#
#   make                 # builds ./libmsbpvdi.so
#   make install         # copies it next to the published sqlsqueeze binary
#                        # (pass PUBLISH_DIR=... to override)
#
# SqlSqueeze also finds libmsbpvdi.so in /opt/mssql/lib, /usr/lib and
# /usr/local/lib, so `sudo make SYSINSTALL=1` works too.

CXX ?= g++
SQLVDI_LIB ?= /opt/mssql/lib
PUBLISH_DIR ?= ../../SqlSqueeze.Cli/bin/Release/net10.0/publish

all: libmsbpvdi.so

# libuuid: SQL Server 2025's libsqlvdi.so calls uuid_generate() but does not link
# libuuid itself (SQL Server has it loaded already; a client does not), so loading
# it fails with "undefined symbol: uuid_generate". Linking libuuid into the shim, by
# soname so no -dev package is needed, puts it in the shim's load group. It is
# harmless on versions that do not need it.
libmsbpvdi.so: vdishim.cpp vdi.h vdierror.h
	$(CXX) -shared -fPIC -O2 -o $@ vdishim.cpp \
		-L$(SQLVDI_LIB) -lsqlvdi \
		-Wl,--no-as-needed -l:libuuid.so.1 \
		-Wl,-rpath,$(SQLVDI_LIB)

install: libmsbpvdi.so
	mkdir -p $(PUBLISH_DIR)
	cp libmsbpvdi.so $(PUBLISH_DIR)/

clean:
	rm -f libmsbpvdi.so

.PHONY: all install clean
