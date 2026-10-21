/* Minimal runtime probe: load native code and exercise QUIC/TLS/H3 allocation.
 * This is not a network handshake or a substitute for device integration tests.
 */
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>

extern const char *_quiche_version(void);
extern bool _quiche_version_is_supported(uint32_t version);
extern void *_quiche_config_new(uint32_t version);
extern int _quiche_config_set_application_protos(void *config, const uint8_t *protos, size_t length);
extern void _quiche_config_free(void *config);
extern void *_quiche_h3_config_new(void);
extern void _quiche_h3_config_free(void *config);

int main(void) {
    const char *version = _quiche_version();
    const uint8_t alpn[] = {2, 'h', '3'};
    if (version == NULL || !_quiche_version_is_supported(1)) {
        fprintf(stderr, "FAIL native version / QUIC v1 support\n");
        return 1;
    }
    for (int iteration = 0; iteration < 100; ++iteration) {
        void *config = _quiche_config_new(1);
        if (config == NULL) {
            fprintf(stderr, "FAIL QUIC/TLS configuration allocation\n");
            return 2;
        }
        int result = _quiche_config_set_application_protos(config, alpn, sizeof(alpn));
        void *h3 = _quiche_h3_config_new();
        _quiche_config_free(config);
        if (result != 0 || h3 == NULL) {
            if (h3 != NULL) _quiche_h3_config_free(h3);
            fprintf(stderr, "FAIL TLS ALPN / HTTP/3 configuration\n");
            return 3;
        }
        _quiche_h3_config_free(h3);
    }
    printf("PASS quiche %s: QUIC v1, TLS ALPN and HTTP/3 configuration (100 iterations)\n", version);
    return 0;
}
