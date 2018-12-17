#include <vector>
#include "crypto/equihash.h"
#include "equihashverify.h"

static const char *default_personalization = "ZcashPoW";

bool verifyEH_200_9(const char *hdr, const std::vector<unsigned char> &soln, const char *personalization){
  unsigned int n = 200;
  unsigned int k = 9;

  if (soln.size() != 1344)
      return false;

  if (personalization == NULL)
      personalization = default_personalization;

  // Hash state
  crypto_generichash_blake2b_state state;
  EhInitialiseState(n, k, state, personalization);

  crypto_generichash_blake2b_update(&state, (const unsigned char*)hdr, 140);

  bool isValid = Eh200_9.IsValidSolution(state, soln);

  return isValid;
}

bool verifyEH_144_5(const char *hdr, int header_length, const std::vector<unsigned char> &soln, const char *personalization) {
    unsigned int n = 144;
    unsigned int k = 5;

    if (soln.size() != 100)
        return false;

    if (personalization == NULL)
        personalization = default_personalization;

    // Hash state
    crypto_generichash_blake2b_state state;
    EhInitialiseState(n, k, state, personalization);

    crypto_generichash_blake2b_update(&state, (const unsigned char*)hdr, header_length);

    bool isValid = Eh144_5.IsValidSolution(state, soln);

    return isValid;
}
