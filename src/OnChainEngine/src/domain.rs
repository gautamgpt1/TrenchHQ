use serde::{Deserialize, Deserializer, Serialize};

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DecimalValue {
    pub coefficient: String,
    pub scale: u32,
}

#[derive(Clone, Debug, Deserialize, Eq, Hash, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AssetKey {
    pub chain_namespace: String,
    pub chain_id: String,
    pub address: String,
}

#[derive(Clone, Debug, Deserialize, Eq, Hash, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DeploymentKey {
    pub chain_namespace: String,
    pub chain_id: String,
    pub protocol_id: String,
    pub contract_address: String,
}

#[derive(Clone, Debug, Eq, Hash, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PoolKey {
    pub deployment_key: DeploymentKey,
    pub pool_id: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CanonicalPoolKey {
    deployment_key: DeploymentKey,
    pool_id: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct LegacySolanaPoolKey {
    chain_namespace: String,
    chain_id: String,
    protocol_id: String,
    pool_address: String,
}

#[derive(Deserialize)]
#[serde(untagged)]
enum PoolKeyRepresentation {
    Canonical(CanonicalPoolKey),
    LegacySolana(LegacySolanaPoolKey),
}

impl<'de> Deserialize<'de> for PoolKey {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        match PoolKeyRepresentation::deserialize(deserializer)? {
            PoolKeyRepresentation::Canonical(key) => Ok(Self {
                deployment_key: key.deployment_key,
                pool_id: key.pool_id,
            }),
            PoolKeyRepresentation::LegacySolana(key) => Ok(Self {
                deployment_key: DeploymentKey {
                    chain_namespace: key.chain_namespace,
                    chain_id: key.chain_id,
                    protocol_id: key.protocol_id,
                    contract_address: String::new(),
                },
                pool_id: key.pool_address,
            }),
        }
    }
}

impl PoolKey {
    pub fn protocol_id(&self) -> &str {
        &self.deployment_key.protocol_id
    }

    pub fn pool_address(&self) -> &str {
        &self.pool_id
    }
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum SupportStatus {
    Supported,
    DiscoveredUnsupported,
    InvalidCandidate,
    TemporarilyUnavailable,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum Commitment {
    Processed,
    Confirmed,
    Finalized,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum RecoveryState {
    Connecting,
    Live,
    Reconnecting,
    Replaying,
    Reconciling,
    SnapshotRequired,
    Stale,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PoolDescriptor {
    pub pool_key: PoolKey,
    pub pool_type: String,
    pub program_id: String,
    pub base_mint: String,
    pub quote_mint: String,
    pub base_decimals: u8,
    pub quote_decimals: u8,
    pub base_vault: Option<String>,
    pub quote_vault: Option<String>,
    #[serde(default)]
    pub protocol_accounts: Vec<ProtocolAccount>,
    pub support_status: SupportStatus,
    pub support_reason: Option<String>,
    #[serde(default)]
    pub asset0: Option<AssetKey>,
    #[serde(default)]
    pub asset1: Option<AssetKey>,
    #[serde(default)]
    pub validation_block: Option<u64>,
    #[serde(default)]
    pub validation_block_hash: Option<String>,
    #[serde(default)]
    pub fee_tier: Option<u32>,
    #[serde(default)]
    pub tick_spacing: Option<i32>,
    #[serde(default)]
    pub bin_step: Option<u16>,
    #[serde(default)]
    pub hook_address: Option<String>,
    #[serde(default)]
    pub pricing_mode: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProtocolAccount {
    pub role: String,
    pub address: String,
    #[serde(default)]
    pub index: Option<u32>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum ChainPosition {
    Solana {
        slot: u64,
    },
    Evm {
        block_number: u64,
        block_hash: String,
        parent_hash: Option<String>,
        transaction_index: Option<u64>,
        log_index: Option<u64>,
    },
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum ChainFinality {
    Solana { commitment: Commitment },
    Evm { status: EvmFinality },
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum EvmFinality {
    Head,
    Safe,
    Finalized,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WatchedPoolSelection {
    pub descriptor: PoolDescriptor,
    pub selected_mint: String,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum TradeDirection {
    Buy,
    Sell,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NormalizedSwap {
    pub event_id: String,
    pub pool_key: PoolKey,
    pub slot: u64,
    pub signature: String,
    pub outer_instruction_index: Option<u16>,
    pub inner_instruction_index: Option<u16>,
    pub log_index: Option<u32>,
    pub direction: TradeDirection,
    pub base_amount_raw: u64,
    pub quote_amount_raw: u64,
    pub commitment: Commitment,
    pub source_id: String,
    pub observed_at_unix_ms: i64,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PriceUpdate {
    pub pool_key: PoolKey,
    pub stream_epoch: u64,
    pub sequence: u64,
    pub last_trade_price_quote: Option<DecimalValue>,
    pub last_trade_event_id: Option<String>,
    pub spot_price_quote: Option<DecimalValue>,
    pub price_sol: Option<DecimalValue>,
    pub price_usd: Option<DecimalValue>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub price_native: Option<DecimalValue>,
    pub quote_mint: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub quote_asset: Option<AssetKey>,
    pub slot: u64,
    pub commitment: Commitment,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub chain_position: Option<ChainPosition>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub finality: Option<ChainFinality>,
    pub source_id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub provider_profile_id: Option<String>,
    pub reference_source_id: Option<String>,
    pub reference_observed_at_unix_ms: Option<i64>,
    pub observed_at_unix_ms: i64,
    pub chain_age_ms: Option<u64>,
    pub stale: bool,
    pub replaying: bool,
    pub recovery_state: RecoveryState,
}
