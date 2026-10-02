/**
 * workbuddy-api — programmatic entry point.
 *
 * Everything the CLI does is available as a library so another tool can embed
 * the credential handling without shelling out:
 *
 * ```js
 * import { CredentialStore, WorkBuddyClient, Catalog, CatalogStore, createGateway } from "workbuddy-api";
 * ```
 *
 * @module workbuddy-api
 */

export { CN_VARIANT, AI_VARIANT, VARIANTS, variantFor, requireVariant, fallbackRoster } from "./variants.js";
export {
  AccountRouter,
  AccountVault,
  accountsPath,
} from "./accounts.js";
export {
  claimDailyCheckin,
  ensureCheckedIn,
  fetchCheckinStatus,
  normalizeCheckinStatus,
} from "./checkin.js";
export {
  CredentialStore,
  credentialIdentity,
  desktopAuthCandidates,
  ownAuthPath,
  parseWorkBuddyAuth,
  regionOf,
} from "./auth.js";
export {
  WorkBuddyClient,
  billingHeaders,
  chatHeaders,
  classifyUpstreamError,
  collectChatCompletion,
  extractDisplayErrorMessage,
  iterateSseData,
  modelWithCurrentPromotion,
  normalizeCredits,
  parseModelCatalog,
  prepareBodyFor,
  prepareChatBody,
  prepareInternationalChatBody,
  probeEffort,
} from "./upstream.js";
export { Catalog, CatalogStore, catalogPath } from "./catalog.js";
export { SearchClient, SearchError, formatSearchResults } from "./search.js";
export {
  AtRestKeyProvider,
  WorkBuddyKeyError,
  classifyDesktopAuthDocument,
  defaultElectronPaths,
  isEncryptedDesktopAuth,
  resolveKeyHelperPath,
  unwrapDesktopAuthDocument,
  WORKBUDDY_ELECTRON_BIN_ENV,
} from "./desktop-credential-protection.js";
export { createGateway } from "./gateway.js";
export {
  createModelResolver,
  estimateTokens,
  pickDefaultModel,
} from "./protocol.js";
export * as anthropic from "./anthropic.js";
export * as responses from "./responses.js";
export { loadConfig, saveConfig, configPath } from "./config.js";
export { stateHome, logPath } from "./paths.js";
export { chatUserAgent, appUserAgent, detectAppInstall, resolveChatIdentity } from "./client-identity.js";
export { run } from "./bin.js";
