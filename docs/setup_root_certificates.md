# Root Certificate Setup Guide (NetBSD)

When downloading files over HTTPS on NetBSD (e.g., `pkg_add`, `ftp`, `git clone`),
you may encounter SSL certificate errors such as:

```
fatal: unable to access '...': SSL: certificate verification failed
```

or:

```
Certificate verification failed for ...
unable to get local issuer certificate
```

This happens because NetBSD does not include Mozilla root certificates (CA certificates)
by default. The following steps install and configure them.

## Option 1: Install mozilla-rootcerts-openssl (Recommended)

The `mozilla-rootcerts-openssl` package installs the certificates and automatically
places them in the OpenSSL certificate directory with hash symlinks:

```sh
pkg_add mozilla-rootcerts-openssl
```

This is the simplest approach — no additional commands are needed after installation.

## Option 2: Manual Setup with mozilla-rootcerts

If you have `mozilla-rootcerts` installed (without the `-openssl` variant), run:

```sh
mozilla-rootcerts install
```

This extracts the certificates to `/etc/openssl/certs` and creates the hash symlinks
needed by OpenSSL.

## NetBSD 10.0 and Later: certctl rehash

NetBSD 10.0 introduced `certctl` for certificate management. After installing
certificates, run:

```sh
certctl rehash
```

This ensures the certificate store is up to date.

## Verification

After installation, verify that HTTPS connections work:

```sh
# Test with ftp (NetBSD's built-in HTTP client)
ftp -o /dev/null https://cdn.netbsd.org/

# Test with pkg_add (if PKG_PATH is set)
pkg_add -n bash
```

## Temporary Workaround (Not Recommended)

If you need to bypass certificate verification temporarily, the following options
are available. **These disable security checks and should only be used as a last resort.**

For `git`:

```sh
git config --global http.sslVerify false
```

For `pkg_add` / `ftp`:

```sh
export FTP_SSL_INSECURE=1
export SSL_NO_VERIFY_PEER=1
```

**Re-enable verification after use:**

```sh
git config --global http.sslVerify true
unset FTP_SSL_INSECURE
unset SSL_NO_VERIFY_PEER
```
