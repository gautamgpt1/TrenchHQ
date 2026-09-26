use crate::domain::{
    ChainFinality, ChainPosition, Commitment, DecimalValue, EvmFinality, PriceUpdate,
    RecoveryState, SupportStatus, WatchedPoolSelection,
};
use crate::evm::chain;
use crate::evm::domain::{FinalityUpdate, HeadUpdate, LogUpdate, Rollback, SnapshotResponse};
use crate::price::{multiply_decimal, PriceMathError};
use crate::protocol::aerodrome_classic;
use crate::protocol::aerodrome_slipstream;
use crate::protocol::curve;
use crate::protocol::fermi_swap;
use crate::protocol::pancake_infinity_bin;
use crate::protocol::pancake_infinity_cl;
use crate::protocol::pancake_v2;
use crate::protocol::pancake_v3;
use crate::protocol::pons_v2_curve;
use crate::protocol::uniswap_v2;
use crate::protocol::uniswap_v3;
use crate::protocol::uniswap_v4;
use num_bigint::BigUint;
use std::collections::{HashMap, VecDeque};
use std::str::FromStr;
use std::time::{SystemTime, UNIX_EPOCH};
use thiserror::Error;

const MAX_BLOCK_TIMESTAMPS: usize = 128;
const MAX_POOL_REVISIONS: usize = 512;
const REFERENCE_PRICE_STALE_AFTER_MS: i64 = 30_000;

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum EvmMarketStateError {
    #[error("unsupported EVM pool {0}")]
    UnsupportedPool(String),
    #[error("unsupported EVM chain or protocol")]
    UnsupportedDeployment,
    #[error("EVM pool descriptor identity is invalid")]
    InvalidDescriptor,
    #[error("EVM update chain does not match the selected pools")]
    WrongChain,
    #[error("EVM snapshot does not contain successful protocol state")]
    InvalidSnapshot,
    #[error("reference price source is empty")]
    EmptyReferenceSource,
    #[error("reference price timestamp is invalid")]
    InvalidReferenceTimestamp,
    #[error("reference asset is invalid")]
    InvalidReferenceAsset,
    #[error(transparent)]
    PriceMath(#[from] PriceMathError),
    #[error(transparent)]
    AerodromeClassic(#[from] aerodrome_classic::DecodeError),
    #[error(transparent)]
    UniswapV2(#[from] uniswap_v2::DecodeError),
    #[error(transparent)]
    UniswapV3(#[from] uniswap_v3::DecodeError),
    #[error(transparent)]
    UniswapV4(#[from] uniswap_v4::DecodeError),
    #[error(transparent)]
    PancakeV3(#[from] pancake_v3::DecodeError),
    #[error(transparent)]
    PancakeInfinityCl(#[from] pancake_infinity_cl::DecodeError),
    #[error(transparent)]
    PancakeInfinityBin(#[from] pancake_infinity_bin::DecodeError),
    #[error(transparent)]
    Curve(#[from] curve::DecodeError),
    #[error(transparent)]
    FermiSwap(#[from] fermi_swap::DecodeError),
    #[error(transparent)]
    PonsV2Curve(#[from] pons_v2_curve::DecodeError),
}

#[derive(Default)]
pub struct EvmMarketState {
    pools: HashMap<crate::domain::PoolKey, PoolRuntime>,
    sessions: HashMap<String, EvmChainSession>,
}

#[derive(Clone)]
struct EvmChainSession {
    stream_epoch: u64,
    recovery_state: RecoveryState,
    block_timestamps: HashMap<String, u64>,
    block_timestamp_order: VecDeque<String>,
    native_usd_fallback: Option<ReferencePrice>,
    asset_usd_references: HashMap<String, ReferencePrice>,
    source_id: String,
    provider_profile_id: Option<String>,
}

impl EvmChainSession {
    fn new(chain_id: &str) -> Result<Self, EvmMarketStateError> {
        let definition = chain::by_chain_id(chain_id).ok_or(EvmMarketStateError::WrongChain)?;
        Ok(Self {
            stream_epoch: current_unix_ms().max(1) as u64,
            recovery_state: RecoveryState::Connecting,
            block_timestamps: HashMap::new(),
            block_timestamp_order: VecDeque::new(),
            native_usd_fallback: None,
            asset_usd_references: HashMap::new(),
            source_id: format!("{}-json-rpc", definition.source_namespace),
            provider_profile_id: None,
        })
    }
}

impl EvmMarketState {
    pub fn watched_pool_count(&self) -> usize {
        self.pools.len()
    }

    pub fn validate_watched_pools(
        selections: &[WatchedPoolSelection],
    ) -> Result<(), EvmMarketStateError> {
        for selection in selections {
            validate_selection(selection)?;
        }
        Ok(())
    }

    pub fn replace_watched_pools(
        &mut self,
        selections: Vec<WatchedPoolSelection>,
    ) -> Result<usize, EvmMarketStateError> {
        Self::validate_watched_pools(&selections)?;
        let mut old = std::mem::take(&mut self.pools);
        let mut replacement = HashMap::with_capacity(selections.len());
        for selection in selections {
            let key = selection.descriptor.pool_key.clone();
            let runtime = if let Some(mut existing) = old.remove(&key) {
                existing.selection = selection;
                existing
            } else {
                PoolRuntime::new(selection)
            };
            replacement.insert(key, runtime);
        }
        self.pools = replacement;
        let chain_ids = self
            .pools
            .keys()
            .map(|key| key.deployment_key.chain_id.clone())
            .collect::<std::collections::HashSet<_>>();
        for chain_id in chain_ids {
            if !self.sessions.contains_key(&chain_id) {
                self.sessions
                    .insert(chain_id.clone(), EvmChainSession::new(&chain_id)?);
            }
        }
        Ok(self.pools.len())
    }

    pub fn set_recovery_state(
        &mut self,
        chain_id: &str,
        state: RecoveryState,
    ) -> Result<(), EvmMarketStateError> {
        self.session_mut(chain_id)?.recovery_state = state;
        Ok(())
    }

    pub fn set_provider_context(
        &mut self,
        chain_id: &str,
        provider_profile_id: String,
        source_id: String,
    ) -> Result<(), EvmMarketStateError> {
        if provider_profile_id.trim().is_empty() || source_id.trim().is_empty() {
            return Err(EvmMarketStateError::InvalidDescriptor);
        }
        let session = self.session_mut(chain_id)?;
        session.provider_profile_id = Some(provider_profile_id);
        session.source_id = source_id;
        Ok(())
    }

    pub fn apply_native_usd_reference(
        &mut self,
        chain_id: &str,
        value: DecimalValue,
        source_id: String,
        observed_at_unix_ms: i64,
    ) -> Result<Vec<PriceUpdate>, EvmMarketStateError> {
        if source_id.trim().is_empty() {
            return Err(EvmMarketStateError::EmptyReferenceSource);
        }
        if observed_at_unix_ms <= 0 {
            return Err(EvmMarketStateError::InvalidReferenceTimestamp);
        }
        multiply_decimal(
            &value,
            &DecimalValue {
                coefficient: "1".to_owned(),
                scale: 0,
            },
        )?;
        self.session_mut(chain_id)?.native_usd_fallback = Some(ReferencePrice {
            value,
            source_id,
            observed_at_unix_ms,
        });
        Ok(self.next_enriched_updates(chain_id))
    }

    pub fn apply_asset_usd_reference(
        &mut self,
        chain_id: &str,
        asset_address: String,
        value: DecimalValue,
        source_id: String,
        observed_at_unix_ms: i64,
    ) -> Result<Vec<PriceUpdate>, EvmMarketStateError> {
        if chain_id != "4663"
            || !uniswap_v2::is_address(&asset_address)
            || source_id.trim().is_empty()
        {
            return Err(EvmMarketStateError::InvalidReferenceAsset);
        }
        if observed_at_unix_ms <= 0 {
            return Err(EvmMarketStateError::InvalidReferenceTimestamp);
        }
        multiply_decimal(
            &value,
            &DecimalValue {
                coefficient: "1".to_owned(),
                scale: 0,
            },
        )?;
        self.session_mut(chain_id)?.asset_usd_references.insert(
            asset_address.to_ascii_lowercase(),
            ReferencePrice {
                value,
                source_id,
                observed_at_unix_ms,
            },
        );
        Ok(self.next_enriched_updates(chain_id))
    }

    pub fn reset_for_snapshot(&mut self, chain_id: &str) -> Result<(), EvmMarketStateError> {
        let session = self.session_mut(chain_id)?;
        session.stream_epoch =
            (current_unix_ms().max(1) as u64).max(session.stream_epoch.saturating_add(1));
        session.block_timestamps.clear();
        session.block_timestamp_order.clear();
        for runtime in self.pools.values_mut().filter(|runtime| {
            runtime
                .selection
                .descriptor
                .pool_key
                .deployment_key
                .chain_id
                == chain_id
        }) {
            runtime.reset();
        }
        Ok(())
    }

    pub fn apply_head(&mut self, update: HeadUpdate) -> Result<(), EvmMarketStateError> {
        ensure_supported_chain(&update.chain_id)?;
        let session = self.session_mut(&update.chain_id)?;
        session.stream_epoch = update.connection_epoch.max(1);
        let hash = update.hash.to_ascii_lowercase();
        if !session.block_timestamps.contains_key(&hash) {
            session.block_timestamp_order.push_back(hash.clone());
        }
        session.block_timestamps.insert(hash, update.timestamp);
        while session.block_timestamp_order.len() > MAX_BLOCK_TIMESTAMPS {
            if let Some(oldest) = session.block_timestamp_order.pop_front() {
                session.block_timestamps.remove(&oldest);
            }
        }
        Ok(())
    }

    pub fn apply_log(
        &mut self,
        update: LogUpdate,
    ) -> Result<Vec<PriceUpdate>, EvmMarketStateError> {
        ensure_supported_chain(&update.chain_id)?;
        self.session_mut(&update.chain_id)?.stream_epoch = update.connection_epoch.max(1);
        let Some(runtime) = self.pools.values_mut().find(|runtime| {
            let pool_key = &runtime.selection.descriptor.pool_key;
            pool_key.deployment_key.chain_id == update.chain_id
                && if is_singleton_protocol(pool_key.protocol_id()) {
                    update
                        .address
                        .eq_ignore_ascii_case(&pool_key.deployment_key.contract_address)
                        && update.topics.get(1).is_some_and(|pool_id| {
                            pool_id.eq_ignore_ascii_case(pool_key.pool_address())
                        })
                } else if pool_key.protocol_id() == fermi_swap::PROTOCOL_ID {
                    update
                        .address
                        .eq_ignore_ascii_case(&pool_key.deployment_key.contract_address)
                        && fermi_swap::matches_pair(
                            &update.topics,
                            &runtime.selection.descriptor.base_mint,
                            &runtime.selection.descriptor.quote_mint,
                        )
                } else {
                    pool_key
                        .pool_address()
                        .eq_ignore_ascii_case(&update.address)
                }
        }) else {
            return Ok(Vec::new());
        };

        let event_id = format!(
            "{}:{}:{}:{}",
            update.chain_id,
            update.block_hash.to_ascii_lowercase(),
            update.transaction_hash.to_ascii_lowercase(),
            update.log_index
        );
        let changed = if update.removed {
            runtime.remove_event(&event_id)
        } else {
            if runtime.contains_event(&event_id) {
                return Ok(Vec::new());
            }
            match runtime.selection.descriptor.pool_key.protocol_id() {
                uniswap_v2::PROTOCOL_ID | pancake_v2::PROTOCOL_ID => match update.topics.first() {
                    Some(topic) if topic.eq_ignore_ascii_case(uniswap_v2::SWAP_TOPIC) => {
                        let swap = uniswap_v2::decode_swap(&update.topics, &update.data)?;
                        runtime.apply_v2_swap(&update, event_id, &swap)?;
                    }
                    Some(topic) if topic.eq_ignore_ascii_case(uniswap_v2::SYNC_TOPIC) => {
                        let reserves = uniswap_v2::decode_sync(&update.data)?;
                        runtime.apply_v2_sync(&update, event_id, &reserves)?;
                    }
                    _ => return Ok(Vec::new()),
                },
                aerodrome_classic::PROTOCOL_ID => match update.topics.first() {
                    Some(topic) if topic.eq_ignore_ascii_case(aerodrome_classic::SWAP_TOPIC) => {
                        let swap = aerodrome_classic::decode_swap(&update.topics, &update.data)?;
                        runtime.apply_aerodrome_classic_swap(&update, event_id, &swap)?;
                    }
                    Some(topic) if topic.eq_ignore_ascii_case(aerodrome_classic::SYNC_TOPIC) => {
                        let reserves = aerodrome_classic::decode_sync(&update.data)?;
                        runtime.apply_aerodrome_classic_reserves(
                            update.block_number,
                            &update.block_hash,
                            Some(update.transaction_index),
                            Some(update.log_index),
                            update.observed_at_unix_ms,
                            Some(event_id),
                            &reserves,
                        )?;
                    }
                    _ => return Ok(Vec::new()),
                },
                aerodrome_slipstream::PROTOCOL_ID => match update.topics.first() {
                    Some(topic) if topic.eq_ignore_ascii_case(aerodrome_slipstream::SWAP_TOPIC) => {
                        let swap = aerodrome_slipstream::decode_swap(&update.topics, &update.data)?;
                        runtime.apply_aerodrome_slipstream_swap(&update, event_id, &swap)?;
                    }
                    _ => return Ok(Vec::new()),
                },
                uniswap_v3::PROTOCOL_ID => match update.topics.first() {
                    Some(topic) if topic.eq_ignore_ascii_case(uniswap_v3::SWAP_TOPIC) => {
                        let swap = uniswap_v3::decode_swap(&update.topics, &update.data)?;
                        runtime.apply_v3_swap(&update, event_id, &swap)?;
                    }
                    _ => return Ok(Vec::new()),
                },
                uniswap_v4::PROTOCOL_ID => match update.topics.first() {
                    Some(topic) if topic.eq_ignore_ascii_case(uniswap_v4::SWAP_TOPIC) => {
                        let swap = uniswap_v4::decode_swap(&update.topics, &update.data)?;
                        runtime.apply_v4_swap(&update, event_id, &swap)?;
                    }
                    _ => return Ok(Vec::new()),
                },
                pancake_v3::PROTOCOL_ID => match update.topics.first() {
                    Some(topic) if topic.eq_ignore_ascii_case(pancake_v3::SWAP_TOPIC) => {
                        let swap = pancake_v3::decode_swap(&update.topics, &update.data)?;
                        runtime.apply_pancake_v3_swap(&update, event_id, &swap)?;
                    }
                    _ => return Ok(Vec::new()),
                },
                pancake_infinity_cl::PROTOCOL_ID => match update.topics.first() {
                    Some(topic) if topic.eq_ignore_ascii_case(pancake_infinity_cl::SWAP_TOPIC) => {
                        let swap = pancake_infinity_cl::decode_swap(&update.topics, &update.data)?;
                        runtime.apply_pancake_infinity_cl_swap(&update, event_id, &swap)?;
                    }
                    _ => return Ok(Vec::new()),
                },
                pancake_infinity_bin::PROTOCOL_ID => match update.topics.first() {
                    Some(topic) if topic.eq_ignore_ascii_case(pancake_infinity_bin::SWAP_TOPIC) => {
                        let swap = pancake_infinity_bin::decode_swap(&update.topics, &update.data)?;
                        runtime.apply_pancake_infinity_bin_swap(&update, event_id, &swap)?;
                    }
                    _ => return Ok(Vec::new()),
                },
                curve::PROTOCOL_ID => match update.topics.first() {
                    Some(topic) if curve::is_token_exchange_topic(topic) => {
                        let exchange = curve::decode_token_exchange(&update.topics, &update.data)?;
                        if !runtime.apply_curve_exchange(&update, event_id, &exchange)? {
                            return Ok(Vec::new());
                        }
                    }
                    _ => return Ok(Vec::new()),
                },
                fermi_swap::PROTOCOL_ID => match update.topics.first() {
                    Some(topic) if fermi_swap::is_swap_topic(topic) => {
                        let swap = fermi_swap::decode_swap(&update.topics, &update.data)?;
                        runtime.apply_fermi_swap(&update, event_id, &swap)?;
                    }
                    _ => return Ok(Vec::new()),
                },
                pons_v2_curve::PROTOCOL_ID => match update.topics.first() {
                    Some(topic) if pons_v2_curve::is_trade_topic(topic) => {
                        let trade = pons_v2_curve::decode_trade(&update.topics, &update.data)?;
                        runtime.apply_pons_v2_trade(&update, event_id, &trade)?;
                    }
                    _ => return Ok(Vec::new()),
                },
                _ => return Err(EvmMarketStateError::UnsupportedDeployment),
            }
            true
        };
        if !changed {
            return Ok(Vec::new());
        }
        let target_key = runtime.selection.descriptor.pool_key.clone();
        let is_reference_pool = is_native_usd_reference_pool(&target_key);
        if is_reference_pool {
            return Ok(self.next_enriched_updates(&update.chain_id));
        }
        let reference = self
            .preferred_native_usd_reference(&target_key.deployment_key.chain_id, current_unix_ms());
        let session = self.session(&target_key.deployment_key.chain_id)?.clone();
        let runtime = self
            .pools
            .get_mut(&target_key)
            .ok_or(EvmMarketStateError::InvalidDescriptor)?;
        let mut price_update = runtime.next_update(
            session.stream_epoch,
            &session.recovery_state,
            &session.block_timestamps,
        );
        apply_reference_prices(
            &mut price_update,
            &reference,
            &session.asset_usd_references,
            current_unix_ms(),
        );
        apply_provider_context(
            &mut price_update,
            &session.source_id,
            session.provider_profile_id.as_deref(),
        );
        Ok(vec![price_update])
    }

    pub fn apply_snapshot(
        &mut self,
        snapshot: SnapshotResponse,
    ) -> Result<Vec<PriceUpdate>, EvmMarketStateError> {
        let chain_id = snapshot.pool_key.deployment_key.chain_id.clone();
        ensure_supported_chain(&chain_id)?;
        self.session_mut(&chain_id)?;
        let runtime = self
            .pools
            .get_mut(&snapshot.pool_key)
            .ok_or(EvmMarketStateError::InvalidDescriptor)?;
        match runtime.selection.descriptor.pool_key.protocol_id() {
            uniswap_v2::PROTOCOL_ID | pancake_v2::PROTOCOL_ID => {
                let call = snapshot
                    .calls
                    .iter()
                    .find(|call| call.id == uniswap_v2::RESERVES_CALL_ID && call.success)
                    .ok_or(EvmMarketStateError::InvalidSnapshot)?;
                let reserves = uniswap_v2::decode_reserves(&call.return_data)?;
                runtime.apply_v2_snapshot(&snapshot, &reserves)?;
            }
            aerodrome_classic::PROTOCOL_ID => {
                let call = snapshot
                    .calls
                    .iter()
                    .find(|call| call.id == aerodrome_classic::RESERVES_CALL_ID && call.success)
                    .ok_or(EvmMarketStateError::InvalidSnapshot)?;
                let reserves = aerodrome_classic::decode_reserves(&call.return_data)?;
                runtime.apply_aerodrome_classic_reserves(
                    snapshot.block_number,
                    &snapshot.block_hash,
                    None,
                    None,
                    current_unix_ms(),
                    None,
                    &reserves,
                )?;
            }
            aerodrome_slipstream::PROTOCOL_ID => {
                let slot0_call = snapshot
                    .calls
                    .iter()
                    .find(|call| call.id == aerodrome_slipstream::SLOT0_CALL_ID && call.success)
                    .ok_or(EvmMarketStateError::InvalidSnapshot)?;
                let liquidity_call = snapshot
                    .calls
                    .iter()
                    .find(|call| call.id == aerodrome_slipstream::LIQUIDITY_CALL_ID && call.success)
                    .ok_or(EvmMarketStateError::InvalidSnapshot)?;
                let slot0 = aerodrome_slipstream::decode_slot0(&slot0_call.return_data)?;
                aerodrome_slipstream::decode_liquidity(&liquidity_call.return_data)?;
                runtime.apply_aerodrome_slipstream_snapshot(&snapshot, &slot0)?;
            }
            uniswap_v3::PROTOCOL_ID => {
                let slot0_call = snapshot
                    .calls
                    .iter()
                    .find(|call| call.id == uniswap_v3::SLOT0_CALL_ID && call.success)
                    .ok_or(EvmMarketStateError::InvalidSnapshot)?;
                let liquidity_call = snapshot
                    .calls
                    .iter()
                    .find(|call| call.id == uniswap_v3::LIQUIDITY_CALL_ID && call.success)
                    .ok_or(EvmMarketStateError::InvalidSnapshot)?;
                let slot0 = uniswap_v3::decode_slot0(&slot0_call.return_data)?;
                uniswap_v3::decode_liquidity(&liquidity_call.return_data)?;
                runtime.apply_v3_snapshot(&snapshot, &slot0)?;
            }
            uniswap_v4::PROTOCOL_ID => {
                let slot0_call = snapshot
                    .calls
                    .iter()
                    .find(|call| call.id == uniswap_v4::SLOT0_CALL_ID && call.success)
                    .ok_or(EvmMarketStateError::InvalidSnapshot)?;
                let liquidity_call = snapshot
                    .calls
                    .iter()
                    .find(|call| call.id == uniswap_v4::LIQUIDITY_CALL_ID && call.success)
                    .ok_or(EvmMarketStateError::InvalidSnapshot)?;
                let slot0 = uniswap_v4::decode_slot0(&slot0_call.return_data)?;
                uniswap_v4::decode_liquidity(&liquidity_call.return_data)?;
                runtime.apply_v4_snapshot(&snapshot, &slot0)?;
            }
            pancake_v3::PROTOCOL_ID => {
                let slot0_call = snapshot
                    .calls
                    .iter()
                    .find(|call| call.id == pancake_v3::SLOT0_CALL_ID && call.success)
                    .ok_or(EvmMarketStateError::InvalidSnapshot)?;
                let liquidity_call = snapshot
                    .calls
                    .iter()
                    .find(|call| call.id == pancake_v3::LIQUIDITY_CALL_ID && call.success)
                    .ok_or(EvmMarketStateError::InvalidSnapshot)?;
                let slot0 = pancake_v3::decode_slot0(&slot0_call.return_data)?;
                pancake_v3::decode_liquidity(&liquidity_call.return_data)?;
                runtime.apply_pancake_v3_snapshot(&snapshot, &slot0)?;
            }
            pancake_infinity_cl::PROTOCOL_ID => {
                let slot0_call = snapshot
                    .calls
                    .iter()
                    .find(|call| call.id == pancake_infinity_cl::SLOT0_CALL_ID && call.success)
                    .ok_or(EvmMarketStateError::InvalidSnapshot)?;
                let liquidity_call = snapshot
                    .calls
                    .iter()
                    .find(|call| call.id == pancake_infinity_cl::LIQUIDITY_CALL_ID && call.success)
                    .ok_or(EvmMarketStateError::InvalidSnapshot)?;
                let slot0 = pancake_infinity_cl::decode_slot0(&slot0_call.return_data)?;
                pancake_infinity_cl::decode_liquidity(&liquidity_call.return_data)?;
                runtime.apply_pancake_infinity_cl_snapshot(&snapshot, &slot0)?;
            }
            pancake_infinity_bin::PROTOCOL_ID => {
                let slot0_call = snapshot
                    .calls
                    .iter()
                    .find(|call| call.id == pancake_infinity_bin::SLOT0_CALL_ID && call.success)
                    .ok_or(EvmMarketStateError::InvalidSnapshot)?;
                let slot0 = pancake_infinity_bin::decode_slot0(&slot0_call.return_data)?;
                runtime.apply_pancake_infinity_bin_snapshot(&snapshot, &slot0)?;
            }
            curve::PROTOCOL_ID => return Ok(Vec::new()),
            fermi_swap::PROTOCOL_ID => return Ok(Vec::new()),
            pons_v2_curve::PROTOCOL_ID => {
                let call = snapshot
                    .calls
                    .iter()
                    .find(|call| call.id == pons_v2_curve::RESERVES_CALL_ID && call.success)
                    .ok_or(EvmMarketStateError::InvalidSnapshot)?;
                let reserves = pons_v2_curve::decode_reserves(&call.return_data)?;
                runtime.apply_pons_v2_snapshot(&snapshot, &reserves)?;
            }
            _ => return Err(EvmMarketStateError::UnsupportedDeployment),
        }
        let target_key = runtime.selection.descriptor.pool_key.clone();
        if is_native_usd_reference_pool(&target_key) {
            return Ok(self.next_enriched_updates(&chain_id));
        }
        let reference = self
            .preferred_native_usd_reference(&target_key.deployment_key.chain_id, current_unix_ms());
        let session = self.session(&chain_id)?.clone();
        let runtime = self
            .pools
            .get_mut(&target_key)
            .ok_or(EvmMarketStateError::InvalidDescriptor)?;
        let mut update = runtime.next_update(
            session.stream_epoch,
            &session.recovery_state,
            &session.block_timestamps,
        );
        apply_reference_prices(
            &mut update,
            &reference,
            &session.asset_usd_references,
            current_unix_ms(),
        );
        apply_provider_context(
            &mut update,
            &session.source_id,
            session.provider_profile_id.as_deref(),
        );
        Ok(vec![update])
    }

    pub fn apply_finality(
        &mut self,
        update: FinalityUpdate,
    ) -> Result<Vec<PriceUpdate>, EvmMarketStateError> {
        ensure_supported_chain(&update.chain_id)?;
        self.session_mut(&update.chain_id)?;
        let session = self.session(&update.chain_id)?.clone();
        let reference = self.preferred_native_usd_reference(&update.chain_id, current_unix_ms());
        let mut updates = Vec::new();
        for runtime in self.pools.values_mut().filter(|runtime| {
            runtime
                .selection
                .descriptor
                .pool_key
                .deployment_key
                .chain_id
                == update.chain_id
        }) {
            if runtime.update_finality(&update) && runtime.values.has_price() {
                let mut price_update = runtime.next_update(
                    session.stream_epoch,
                    &session.recovery_state,
                    &session.block_timestamps,
                );
                apply_reference_prices(
                    &mut price_update,
                    &reference,
                    &session.asset_usd_references,
                    current_unix_ms(),
                );
                apply_provider_context(
                    &mut price_update,
                    &session.source_id,
                    session.provider_profile_id.as_deref(),
                );
                updates.push(price_update);
            }
        }
        Ok(updates)
    }

    pub fn apply_rollback(
        &mut self,
        rollback: Rollback,
    ) -> Result<Vec<PriceUpdate>, EvmMarketStateError> {
        ensure_supported_chain(&rollback.chain_id)?;
        self.session_mut(&rollback.chain_id)?;
        let session = self.session(&rollback.chain_id)?.clone();
        let mut changed_keys = Vec::new();
        for (key, runtime) in self
            .pools
            .iter_mut()
            .filter(|(key, _)| key.deployment_key.chain_id == rollback.chain_id)
        {
            if runtime.rollback(rollback.to_block_number, &rollback.to_block_hash) {
                changed_keys.push(key.clone());
            }
        }
        if changed_keys.iter().any(is_native_usd_reference_pool) {
            return Ok(self
                .next_enriched_updates_for_state(&rollback.chain_id, &RecoveryState::Reconciling));
        }
        let reference = self.preferred_native_usd_reference(&rollback.chain_id, current_unix_ms());
        let mut updates = Vec::new();
        for key in changed_keys {
            if let Some(runtime) = self.pools.get_mut(&key) {
                let mut update = runtime.next_update(
                    session.stream_epoch,
                    &RecoveryState::Reconciling,
                    &session.block_timestamps,
                );
                apply_reference_prices(
                    &mut update,
                    &reference,
                    &session.asset_usd_references,
                    current_unix_ms(),
                );
                apply_provider_context(
                    &mut update,
                    &session.source_id,
                    session.provider_profile_id.as_deref(),
                );
                updates.push(update);
            }
        }
        Ok(updates)
    }

    pub fn snapshots(&self) -> Vec<PriceUpdate> {
        let now = current_unix_ms();
        self.pools
            .values()
            .map(|runtime| {
                let chain_id = &runtime
                    .selection
                    .descriptor
                    .pool_key
                    .deployment_key
                    .chain_id;
                let session = self
                    .sessions
                    .get(chain_id)
                    .expect("validated watched pools have a chain session");
                let reference = self.preferred_native_usd_reference(chain_id, now);
                let mut update = runtime.build_update(
                    session.stream_epoch,
                    runtime.sequence,
                    &session.recovery_state,
                    &session.block_timestamps,
                );
                apply_reference_prices(&mut update, &reference, &session.asset_usd_references, now);
                apply_provider_context(
                    &mut update,
                    &session.source_id,
                    session.provider_profile_id.as_deref(),
                );
                update
            })
            .collect()
    }

    fn next_enriched_updates(&mut self, chain_id: &str) -> Vec<PriceUpdate> {
        let Ok(session) = self.session(chain_id) else {
            return Vec::new();
        };
        let state = session.recovery_state.clone();
        self.next_enriched_updates_for_state(chain_id, &state)
    }

    fn next_enriched_updates_for_state(
        &mut self,
        chain_id: &str,
        recovery_state: &RecoveryState,
    ) -> Vec<PriceUpdate> {
        let now = current_unix_ms();
        let reference = self.preferred_native_usd_reference(chain_id, now);
        let Ok(session) = self.session(chain_id).cloned() else {
            return Vec::new();
        };
        self.pools
            .values_mut()
            .filter(|runtime| {
                runtime.values.has_price()
                    && runtime
                        .selection
                        .descriptor
                        .pool_key
                        .deployment_key
                        .chain_id
                        == chain_id
            })
            .map(|runtime| {
                let mut update = runtime.next_update(
                    session.stream_epoch,
                    recovery_state,
                    &session.block_timestamps,
                );
                apply_reference_prices(&mut update, &reference, &session.asset_usd_references, now);
                apply_provider_context(
                    &mut update,
                    &session.source_id,
                    session.provider_profile_id.as_deref(),
                );
                update
            })
            .collect()
    }

    fn preferred_native_usd_reference(
        &self,
        chain_id: &str,
        now_unix_ms: i64,
    ) -> NativeUsdReferenceDecision {
        let Some(definition) = chain::by_chain_id(chain_id) else {
            return NativeUsdReferenceDecision::default();
        };
        let on_chain = self.pools.values().find_map(|runtime| {
            if runtime
                .selection
                .descriptor
                .pool_key
                .deployment_key
                .chain_id
                != chain_id
                || !is_native_usd_reference_pool(&runtime.selection.descriptor.pool_key)
            {
                return None;
            }
            let value = runtime
                .values
                .last_trade_price
                .as_ref()
                .or(runtime.values.spot_price.as_ref())?
                .clone();
            Some(ReferencePrice {
                value,
                source_id: definition.native_usd_reference_source.to_owned(),
                observed_at_unix_ms: runtime.values.observed_at_unix_ms,
            })
        });
        let fresh_on_chain =
            on_chain.filter(|reference| !reference_is_stale(reference, now_unix_ms));
        let fresh_fallback = self
            .sessions
            .get(chain_id)
            .and_then(|session| session.native_usd_fallback.as_ref())
            .filter(|reference| !reference_is_stale(reference, now_unix_ms))
            .cloned();
        if let (Some(primary), Some(fallback)) = (&fresh_on_chain, &fresh_fallback) {
            if references_disagree(&primary.value, &fallback.value) {
                return NativeUsdReferenceDecision {
                    selected: None,
                    disagreement_observed_at_unix_ms: Some(
                        primary
                            .observed_at_unix_ms
                            .max(fallback.observed_at_unix_ms),
                    ),
                };
            }
        }
        NativeUsdReferenceDecision {
            selected: fresh_on_chain.or(fresh_fallback),
            disagreement_observed_at_unix_ms: None,
        }
    }

    fn session(&self, chain_id: &str) -> Result<&EvmChainSession, EvmMarketStateError> {
        self.sessions
            .get(chain_id)
            .ok_or(EvmMarketStateError::WrongChain)
    }

    fn session_mut(&mut self, chain_id: &str) -> Result<&mut EvmChainSession, EvmMarketStateError> {
        ensure_supported_chain(chain_id)?;
        if !self.sessions.contains_key(chain_id) {
            self.sessions
                .insert(chain_id.to_owned(), EvmChainSession::new(chain_id)?);
        }
        self.sessions
            .get_mut(chain_id)
            .ok_or(EvmMarketStateError::WrongChain)
    }
}

#[derive(Clone)]
struct ReferencePrice {
    value: DecimalValue,
    source_id: String,
    observed_at_unix_ms: i64,
}

#[derive(Clone, Default)]
struct NativeUsdReferenceDecision {
    selected: Option<ReferencePrice>,
    disagreement_observed_at_unix_ms: Option<i64>,
}

#[derive(Clone)]
struct PoolValues {
    last_trade_price: Option<crate::domain::DecimalValue>,
    last_trade_event_id: Option<String>,
    spot_price: Option<crate::domain::DecimalValue>,
    block_number: u64,
    block_hash: String,
    transaction_index: Option<u64>,
    log_index: Option<u64>,
    observed_at_unix_ms: i64,
    finality: EvmFinality,
}

impl Default for PoolValues {
    fn default() -> Self {
        Self {
            last_trade_price: None,
            last_trade_event_id: None,
            spot_price: None,
            block_number: 0,
            block_hash: String::new(),
            transaction_index: None,
            log_index: None,
            observed_at_unix_ms: 0,
            finality: EvmFinality::Head,
        }
    }
}

impl PoolValues {
    fn has_price(&self) -> bool {
        self.last_trade_price.is_some() || self.spot_price.is_some()
    }
}

#[derive(Clone)]
struct Revision {
    event_id: Option<String>,
    values: PoolValues,
}

struct PoolRuntime {
    selection: WatchedPoolSelection,
    sequence: u64,
    values: PoolValues,
    revisions: VecDeque<Revision>,
}

impl PoolRuntime {
    fn new(selection: WatchedPoolSelection) -> Self {
        Self {
            selection,
            sequence: 0,
            values: PoolValues::default(),
            revisions: VecDeque::new(),
        }
    }

    fn reset(&mut self) {
        self.values = PoolValues::default();
        self.revisions.clear();
    }

    fn selected_is_token0(&self) -> Result<bool, EvmMarketStateError> {
        let descriptor = &self.selection.descriptor;
        let token0 = descriptor
            .asset0
            .as_ref()
            .ok_or(EvmMarketStateError::InvalidDescriptor)?;
        Ok(token0.address.eq_ignore_ascii_case(&descriptor.base_mint))
    }

    fn contains_event(&self, event_id: &str) -> bool {
        self.revisions
            .iter()
            .any(|revision| revision.event_id.as_deref() == Some(event_id))
    }

    fn apply_v2_swap(
        &mut self,
        update: &LogUpdate,
        event_id: String,
        swap: &uniswap_v2::Swap,
    ) -> Result<(), EvmMarketStateError> {
        self.values.last_trade_price = Some(uniswap_v2::executed_price(
            swap,
            self.selected_is_token0()?,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.last_trade_event_id = Some(event_id.clone());
        self.values.block_number = update.block_number;
        self.values.block_hash = update.block_hash.to_ascii_lowercase();
        self.values.transaction_index = Some(update.transaction_index);
        self.values.log_index = Some(update.log_index);
        self.values.observed_at_unix_ms = update.observed_at_unix_ms;
        self.values.finality = EvmFinality::Head;
        self.push_revision(Some(event_id));
        Ok(())
    }

    fn apply_curve_exchange(
        &mut self,
        update: &LogUpdate,
        event_id: String,
        exchange: &curve::TokenExchange,
    ) -> Result<bool, EvmMarketStateError> {
        let (base_index, quote_index) = self.curve_coin_indices()?;
        let Some(price) = curve::executed_price(
            exchange,
            base_index,
            quote_index,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?
        else {
            return Ok(false);
        };
        self.values.last_trade_price = Some(price);
        self.values.last_trade_event_id = Some(event_id.clone());
        self.values.block_number = update.block_number;
        self.values.block_hash = update.block_hash.to_ascii_lowercase();
        self.values.transaction_index = Some(update.transaction_index);
        self.values.log_index = Some(update.log_index);
        self.values.observed_at_unix_ms = update.observed_at_unix_ms;
        self.values.finality = EvmFinality::Head;
        self.push_revision(Some(event_id));
        Ok(true)
    }

    fn apply_fermi_swap(
        &mut self,
        update: &LogUpdate,
        event_id: String,
        swap: &fermi_swap::Swap,
    ) -> Result<(), EvmMarketStateError> {
        self.values.last_trade_price = Some(fermi_swap::executed_price(
            swap,
            &self.selection.descriptor.base_mint,
            &self.selection.descriptor.quote_mint,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.last_trade_event_id = Some(event_id.clone());
        self.values.block_number = update.block_number;
        self.values.block_hash = update.block_hash.to_ascii_lowercase();
        self.values.transaction_index = Some(update.transaction_index);
        self.values.log_index = Some(update.log_index);
        self.values.observed_at_unix_ms = update.observed_at_unix_ms;
        self.values.finality = EvmFinality::Head;
        self.push_revision(Some(event_id));
        Ok(())
    }

    fn apply_pons_v2_trade(
        &mut self,
        update: &LogUpdate,
        event_id: String,
        trade: &pons_v2_curve::Trade,
    ) -> Result<(), EvmMarketStateError> {
        self.values.last_trade_price = Some(pons_v2_curve::executed_price(
            trade,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.last_trade_event_id = Some(event_id.clone());
        self.values.block_number = update.block_number;
        self.values.block_hash = update.block_hash.to_ascii_lowercase();
        self.values.transaction_index = Some(update.transaction_index);
        self.values.log_index = Some(update.log_index);
        self.values.observed_at_unix_ms = update.observed_at_unix_ms;
        self.values.finality = EvmFinality::Head;
        self.push_revision(Some(event_id));
        Ok(())
    }

    fn apply_pons_v2_snapshot(
        &mut self,
        snapshot: &SnapshotResponse,
        reserves: &pons_v2_curve::Reserves,
    ) -> Result<(), EvmMarketStateError> {
        self.values.spot_price = Some(pons_v2_curve::spot_price(
            reserves,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.block_number = snapshot.block_number;
        self.values.block_hash = snapshot.block_hash.to_ascii_lowercase();
        self.values.transaction_index = None;
        self.values.log_index = None;
        self.values.observed_at_unix_ms = current_unix_ms();
        self.values.finality = EvmFinality::Head;
        self.push_revision(None);
        Ok(())
    }

    fn curve_coin_indices(&self) -> Result<(u32, u32), EvmMarketStateError> {
        let descriptor = &self.selection.descriptor;
        let base = descriptor
            .protocol_accounts
            .iter()
            .find(|account| {
                account.role == "baseCoin"
                    && account.address.eq_ignore_ascii_case(&descriptor.base_mint)
            })
            .and_then(|account| account.index)
            .ok_or(EvmMarketStateError::InvalidDescriptor)?;
        let quote = descriptor
            .protocol_accounts
            .iter()
            .find(|account| {
                account.role == "quoteCoin"
                    && account.address.eq_ignore_ascii_case(&descriptor.quote_mint)
            })
            .and_then(|account| account.index)
            .ok_or(EvmMarketStateError::InvalidDescriptor)?;
        if base == quote || base > 7 || quote > 7 {
            return Err(EvmMarketStateError::InvalidDescriptor);
        }
        Ok((base, quote))
    }

    fn apply_v2_snapshot(
        &mut self,
        snapshot: &SnapshotResponse,
        reserves: &uniswap_v2::Reserves,
    ) -> Result<(), EvmMarketStateError> {
        self.values.spot_price = Some(uniswap_v2::spot_price(
            reserves,
            self.selected_is_token0()?,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.block_number = snapshot.block_number;
        self.values.block_hash = snapshot.block_hash.to_ascii_lowercase();
        self.values.transaction_index = None;
        self.values.log_index = None;
        self.values.observed_at_unix_ms = current_unix_ms();
        self.values.finality = EvmFinality::Head;
        self.push_revision(None);
        Ok(())
    }

    fn apply_v2_sync(
        &mut self,
        update: &LogUpdate,
        event_id: String,
        reserves: &uniswap_v2::Reserves,
    ) -> Result<(), EvmMarketStateError> {
        self.values.spot_price = Some(uniswap_v2::spot_price(
            reserves,
            self.selected_is_token0()?,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.block_number = update.block_number;
        self.values.block_hash = update.block_hash.to_ascii_lowercase();
        self.values.transaction_index = Some(update.transaction_index);
        self.values.log_index = Some(update.log_index);
        self.values.observed_at_unix_ms = update.observed_at_unix_ms;
        self.values.finality = EvmFinality::Head;
        self.push_revision(Some(event_id));
        Ok(())
    }

    fn apply_v3_swap(
        &mut self,
        update: &LogUpdate,
        event_id: String,
        swap: &uniswap_v3::Swap,
    ) -> Result<(), EvmMarketStateError> {
        let selected_is_token0 = self.selected_is_token0()?;
        self.values.last_trade_price = Some(uniswap_v3::executed_price(
            swap,
            selected_is_token0,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.spot_price = Some(uniswap_v3::spot_price(
            &swap.sqrt_price_x96,
            selected_is_token0,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.last_trade_event_id = Some(event_id.clone());
        self.values.block_number = update.block_number;
        self.values.block_hash = update.block_hash.to_ascii_lowercase();
        self.values.transaction_index = Some(update.transaction_index);
        self.values.log_index = Some(update.log_index);
        self.values.observed_at_unix_ms = update.observed_at_unix_ms;
        self.values.finality = EvmFinality::Head;
        self.push_revision(Some(event_id));
        Ok(())
    }

    fn apply_aerodrome_classic_swap(
        &mut self,
        update: &LogUpdate,
        event_id: String,
        swap: &aerodrome_classic::Swap,
    ) -> Result<(), EvmMarketStateError> {
        self.values.last_trade_price = Some(aerodrome_classic::executed_price(
            swap,
            self.selected_is_token0()?,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.last_trade_event_id = Some(event_id.clone());
        self.values.block_number = update.block_number;
        self.values.block_hash = update.block_hash.to_ascii_lowercase();
        self.values.transaction_index = Some(update.transaction_index);
        self.values.log_index = Some(update.log_index);
        self.values.observed_at_unix_ms = update.observed_at_unix_ms;
        self.values.finality = EvmFinality::Head;
        self.push_revision(Some(event_id));
        Ok(())
    }

    #[allow(clippy::too_many_arguments)]
    fn apply_aerodrome_classic_reserves(
        &mut self,
        block_number: u64,
        block_hash: &str,
        transaction_index: Option<u64>,
        log_index: Option<u64>,
        observed_at_unix_ms: i64,
        event_id: Option<String>,
        reserves: &aerodrome_classic::Reserves,
    ) -> Result<(), EvmMarketStateError> {
        let selected_is_token0 = self.selected_is_token0()?;
        self.values.spot_price = Some(match self.selection.descriptor.pool_type.as_str() {
            "volatile" => aerodrome_classic::volatile_spot_price(
                reserves,
                selected_is_token0,
                self.selection.descriptor.base_decimals,
                self.selection.descriptor.quote_decimals,
            )?,
            "stable" => {
                let (token0_decimals, token1_decimals) = if selected_is_token0 {
                    (
                        self.selection.descriptor.base_decimals,
                        self.selection.descriptor.quote_decimals,
                    )
                } else {
                    (
                        self.selection.descriptor.quote_decimals,
                        self.selection.descriptor.base_decimals,
                    )
                };
                aerodrome_classic::stable_spot_price(
                    reserves,
                    selected_is_token0,
                    token0_decimals,
                    token1_decimals,
                )?
            }
            _ => return Err(EvmMarketStateError::InvalidDescriptor),
        });
        self.values.block_number = block_number;
        self.values.block_hash = block_hash.to_ascii_lowercase();
        self.values.transaction_index = transaction_index;
        self.values.log_index = log_index;
        self.values.observed_at_unix_ms = observed_at_unix_ms;
        self.values.finality = EvmFinality::Head;
        self.push_revision(event_id);
        Ok(())
    }

    fn apply_aerodrome_slipstream_swap(
        &mut self,
        update: &LogUpdate,
        event_id: String,
        swap: &aerodrome_slipstream::Swap,
    ) -> Result<(), EvmMarketStateError> {
        let selected_is_token0 = self.selected_is_token0()?;
        self.values.last_trade_price = Some(aerodrome_slipstream::executed_price(
            swap,
            selected_is_token0,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.spot_price = Some(aerodrome_slipstream::spot_price(
            &swap.sqrt_price_x96,
            selected_is_token0,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.last_trade_event_id = Some(event_id.clone());
        self.values.block_number = update.block_number;
        self.values.block_hash = update.block_hash.to_ascii_lowercase();
        self.values.transaction_index = Some(update.transaction_index);
        self.values.log_index = Some(update.log_index);
        self.values.observed_at_unix_ms = update.observed_at_unix_ms;
        self.values.finality = EvmFinality::Head;
        self.push_revision(Some(event_id));
        Ok(())
    }

    fn apply_aerodrome_slipstream_snapshot(
        &mut self,
        snapshot: &SnapshotResponse,
        slot0: &aerodrome_slipstream::Slot0,
    ) -> Result<(), EvmMarketStateError> {
        self.values.spot_price = Some(aerodrome_slipstream::spot_price(
            &slot0.sqrt_price_x96,
            self.selected_is_token0()?,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.block_number = snapshot.block_number;
        self.values.block_hash = snapshot.block_hash.to_ascii_lowercase();
        self.values.transaction_index = None;
        self.values.log_index = None;
        self.values.observed_at_unix_ms = current_unix_ms();
        self.values.finality = EvmFinality::Head;
        self.push_revision(None);
        Ok(())
    }

    fn apply_v3_snapshot(
        &mut self,
        snapshot: &SnapshotResponse,
        slot0: &uniswap_v3::Slot0,
    ) -> Result<(), EvmMarketStateError> {
        self.values.spot_price = Some(uniswap_v3::spot_price(
            &slot0.sqrt_price_x96,
            self.selected_is_token0()?,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.block_number = snapshot.block_number;
        self.values.block_hash = snapshot.block_hash.to_ascii_lowercase();
        self.values.transaction_index = None;
        self.values.log_index = None;
        self.values.observed_at_unix_ms = current_unix_ms();
        self.values.finality = EvmFinality::Head;
        self.push_revision(None);
        Ok(())
    }

    fn apply_v4_swap(
        &mut self,
        update: &LogUpdate,
        event_id: String,
        swap: &uniswap_v4::Swap,
    ) -> Result<(), EvmMarketStateError> {
        let selected_is_token0 = self.selected_is_token0()?;
        if self.selection.descriptor.pricing_mode.as_deref() == Some("spotOnly") {
            self.values.last_trade_price = None;
            self.values.last_trade_event_id = None;
        } else {
            self.values.last_trade_price = Some(uniswap_v4::executed_price(
                swap,
                selected_is_token0,
                self.selection.descriptor.base_decimals,
                self.selection.descriptor.quote_decimals,
            )?);
            self.values.last_trade_event_id = Some(event_id.clone());
        }
        self.values.spot_price = Some(uniswap_v4::spot_price(
            &swap.sqrt_price_x96,
            selected_is_token0,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.block_number = update.block_number;
        self.values.block_hash = update.block_hash.to_ascii_lowercase();
        self.values.transaction_index = Some(update.transaction_index);
        self.values.log_index = Some(update.log_index);
        self.values.observed_at_unix_ms = update.observed_at_unix_ms;
        self.values.finality = EvmFinality::Head;
        self.push_revision(Some(event_id));
        Ok(())
    }

    fn apply_v4_snapshot(
        &mut self,
        snapshot: &SnapshotResponse,
        slot0: &uniswap_v4::Slot0,
    ) -> Result<(), EvmMarketStateError> {
        self.values.spot_price = Some(uniswap_v4::spot_price(
            &slot0.sqrt_price_x96,
            self.selected_is_token0()?,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.block_number = snapshot.block_number;
        self.values.block_hash = snapshot.block_hash.to_ascii_lowercase();
        self.values.transaction_index = None;
        self.values.log_index = None;
        self.values.observed_at_unix_ms = current_unix_ms();
        self.values.finality = EvmFinality::Head;
        self.push_revision(None);
        Ok(())
    }

    fn apply_pancake_v3_swap(
        &mut self,
        update: &LogUpdate,
        event_id: String,
        swap: &pancake_v3::Swap,
    ) -> Result<(), EvmMarketStateError> {
        let selected_is_token0 = self.selected_is_token0()?;
        self.values.last_trade_price = Some(pancake_v3::executed_price(
            swap,
            selected_is_token0,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.spot_price = Some(pancake_v3::spot_price(
            &swap.sqrt_price_x96,
            selected_is_token0,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.apply_concentrated_event_position(update, event_id);
        Ok(())
    }

    fn apply_pancake_v3_snapshot(
        &mut self,
        snapshot: &SnapshotResponse,
        slot0: &pancake_v3::Slot0,
    ) -> Result<(), EvmMarketStateError> {
        self.values.spot_price = Some(pancake_v3::spot_price(
            &slot0.sqrt_price_x96,
            self.selected_is_token0()?,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.apply_concentrated_snapshot_position(snapshot);
        Ok(())
    }

    fn apply_pancake_infinity_cl_swap(
        &mut self,
        update: &LogUpdate,
        event_id: String,
        swap: &pancake_infinity_cl::Swap,
    ) -> Result<(), EvmMarketStateError> {
        let selected_is_token0 = self.selected_is_token0()?;
        self.values.last_trade_price = Some(pancake_infinity_cl::executed_price(
            swap,
            selected_is_token0,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.spot_price = Some(pancake_infinity_cl::spot_price(
            &swap.sqrt_price_x96,
            selected_is_token0,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.apply_concentrated_event_position(update, event_id);
        Ok(())
    }

    fn apply_pancake_infinity_cl_snapshot(
        &mut self,
        snapshot: &SnapshotResponse,
        slot0: &pancake_infinity_cl::Slot0,
    ) -> Result<(), EvmMarketStateError> {
        self.values.spot_price = Some(pancake_infinity_cl::spot_price(
            &slot0.sqrt_price_x96,
            self.selected_is_token0()?,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.apply_concentrated_snapshot_position(snapshot);
        Ok(())
    }

    fn apply_pancake_infinity_bin_swap(
        &mut self,
        update: &LogUpdate,
        event_id: String,
        swap: &pancake_infinity_bin::Swap,
    ) -> Result<(), EvmMarketStateError> {
        let selected_is_token0 = self.selected_is_token0()?;
        let bin_step = self
            .selection
            .descriptor
            .bin_step
            .ok_or(EvmMarketStateError::InvalidDescriptor)?;
        self.values.last_trade_price = Some(pancake_infinity_bin::executed_price(
            swap,
            selected_is_token0,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.values.spot_price = Some(pancake_infinity_bin::spot_price(
            swap.active_id,
            bin_step,
            selected_is_token0,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.apply_concentrated_event_position(update, event_id);
        Ok(())
    }

    fn apply_pancake_infinity_bin_snapshot(
        &mut self,
        snapshot: &SnapshotResponse,
        slot0: &pancake_infinity_bin::Slot0,
    ) -> Result<(), EvmMarketStateError> {
        let bin_step = self
            .selection
            .descriptor
            .bin_step
            .ok_or(EvmMarketStateError::InvalidDescriptor)?;
        self.values.spot_price = Some(pancake_infinity_bin::spot_price(
            slot0.active_id,
            bin_step,
            self.selected_is_token0()?,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        self.apply_concentrated_snapshot_position(snapshot);
        Ok(())
    }

    fn apply_concentrated_event_position(&mut self, update: &LogUpdate, event_id: String) {
        self.values.last_trade_event_id = Some(event_id.clone());
        self.values.block_number = update.block_number;
        self.values.block_hash = update.block_hash.to_ascii_lowercase();
        self.values.transaction_index = Some(update.transaction_index);
        self.values.log_index = Some(update.log_index);
        self.values.observed_at_unix_ms = update.observed_at_unix_ms;
        self.values.finality = EvmFinality::Head;
        self.push_revision(Some(event_id));
    }

    fn apply_concentrated_snapshot_position(&mut self, snapshot: &SnapshotResponse) {
        self.values.block_number = snapshot.block_number;
        self.values.block_hash = snapshot.block_hash.to_ascii_lowercase();
        self.values.transaction_index = None;
        self.values.log_index = None;
        self.values.observed_at_unix_ms = current_unix_ms();
        self.values.finality = EvmFinality::Head;
        self.push_revision(None);
    }

    fn push_revision(&mut self, event_id: Option<String>) {
        self.revisions.push_back(Revision {
            event_id,
            values: self.values.clone(),
        });
        while self.revisions.len() > MAX_POOL_REVISIONS {
            self.revisions.pop_front();
        }
    }

    fn remove_event(&mut self, event_id: &str) -> bool {
        let Some(index) = self
            .revisions
            .iter()
            .position(|revision| revision.event_id.as_deref() == Some(event_id))
        else {
            return false;
        };
        self.revisions.truncate(index);
        self.values = self
            .revisions
            .back()
            .map(|revision| revision.values.clone())
            .unwrap_or_default();
        true
    }

    fn rollback(&mut self, block_number: u64, block_hash: &str) -> bool {
        let previous = self.values.clone();
        while self.revisions.back().is_some_and(|revision| {
            revision.values.block_number > block_number
                || (revision.values.block_number == block_number
                    && !revision.values.block_hash.eq_ignore_ascii_case(block_hash))
        }) {
            self.revisions.pop_back();
        }
        self.values = self
            .revisions
            .back()
            .map(|revision| revision.values.clone())
            .unwrap_or_default();
        previous.block_number != self.values.block_number
            || previous.block_hash != self.values.block_hash
            || previous.last_trade_event_id != self.values.last_trade_event_id
            || previous.spot_price != self.values.spot_price
    }

    fn update_finality(&mut self, update: &FinalityUpdate) -> bool {
        let next = if update
            .finalized
            .as_ref()
            .is_some_and(|block| block.number >= self.values.block_number)
        {
            EvmFinality::Finalized
        } else if update
            .safe
            .as_ref()
            .is_some_and(|block| block.number >= self.values.block_number)
        {
            EvmFinality::Safe
        } else {
            EvmFinality::Head
        };
        if finality_rank(&next) <= finality_rank(&self.values.finality) {
            return false;
        }
        self.values.finality = next;
        true
    }

    fn next_update(
        &mut self,
        stream_epoch: u64,
        recovery_state: &RecoveryState,
        block_timestamps: &HashMap<String, u64>,
    ) -> PriceUpdate {
        self.sequence = self.sequence.saturating_add(1);
        self.build_update(
            stream_epoch,
            self.sequence,
            recovery_state,
            block_timestamps,
        )
    }

    fn build_update(
        &self,
        stream_epoch: u64,
        sequence: u64,
        recovery_state: &RecoveryState,
        block_timestamps: &HashMap<String, u64>,
    ) -> PriceUpdate {
        let descriptor = &self.selection.descriptor;
        let finality = self.values.finality.clone();
        let commitment = match finality {
            EvmFinality::Head => Commitment::Processed,
            EvmFinality::Safe => Commitment::Confirmed,
            EvmFinality::Finalized => Commitment::Finalized,
        };
        let chain_age_ms = block_timestamps
            .get(&self.values.block_hash)
            .map(|seconds| current_unix_ms().saturating_sub((*seconds as i64).saturating_mul(1000)))
            .and_then(|age| u64::try_from(age).ok());
        let primary = self
            .values
            .last_trade_price
            .as_ref()
            .or(self.values.spot_price.as_ref());
        let price_native = chain::by_chain_id(&descriptor.pool_key.deployment_key.chain_id)
            .filter(|definition| {
                descriptor
                    .quote_mint
                    .eq_ignore_ascii_case(definition.wrapped_native_asset_address)
                    || descriptor
                        .quote_mint
                        .eq_ignore_ascii_case(definition.native_asset_address)
            })
            .and_then(|_| primary.cloned());
        PriceUpdate {
            pool_key: descriptor.pool_key.clone(),
            stream_epoch,
            sequence,
            last_trade_price_quote: self.values.last_trade_price.clone(),
            last_trade_event_id: self.values.last_trade_event_id.clone(),
            spot_price_quote: self.values.spot_price.clone(),
            price_sol: None,
            price_usd: None,
            price_native,
            quote_mint: descriptor.quote_mint.clone(),
            quote_asset: descriptor.asset0.as_ref().and_then(|asset0| {
                if asset0.address.eq_ignore_ascii_case(&descriptor.quote_mint) {
                    Some(asset0.clone())
                } else {
                    descriptor.asset1.clone()
                }
            }),
            slot: self.values.block_number,
            commitment,
            chain_position: Some(ChainPosition::Evm {
                block_number: self.values.block_number,
                block_hash: self.values.block_hash.clone(),
                parent_hash: None,
                transaction_index: self.values.transaction_index,
                log_index: self.values.log_index,
            }),
            finality: Some(ChainFinality::Evm { status: finality }),
            source_id: "ethereum-json-rpc".to_owned(),
            provider_profile_id: None,
            reference_source_id: None,
            reference_observed_at_unix_ms: None,
            observed_at_unix_ms: self.values.observed_at_unix_ms,
            chain_age_ms,
            stale: matches!(recovery_state, RecoveryState::Stale),
            replaying: matches!(recovery_state, RecoveryState::Replaying),
            recovery_state: recovery_state.clone(),
        }
    }
}

fn apply_reference_prices(
    update: &mut PriceUpdate,
    native_usd_reference: &NativeUsdReferenceDecision,
    asset_usd_references: &HashMap<String, ReferencePrice>,
    now_unix_ms: i64,
) {
    let Some(primary) = update
        .last_trade_price_quote
        .as_ref()
        .or(update.spot_price_quote.as_ref())
    else {
        return;
    };
    let Some(definition) = chain::by_chain_id(&update.pool_key.deployment_key.chain_id) else {
        return;
    };
    if let Some(observed_at) = native_usd_reference.disagreement_observed_at_unix_ms {
        update.reference_source_id = Some(format!(
            "{}:reference-disagreement",
            definition.source_namespace
        ));
        update.reference_observed_at_unix_ms = Some(observed_at);
        return;
    }
    if update
        .quote_mint
        .eq_ignore_ascii_case(definition.wrapped_native_asset_address)
        || update
            .quote_mint
            .eq_ignore_ascii_case(definition.native_asset_address)
    {
        update.price_native = Some(primary.clone());
        let Some(reference) = native_usd_reference.selected.as_ref() else {
            return;
        };
        if reference_is_stale(reference, now_unix_ms) {
            return;
        }
        if let Ok(usd) = multiply_decimal(primary, &reference.value) {
            update.price_usd = Some(usd);
            update.reference_source_id = Some(reference.source_id.clone());
            update.reference_observed_at_unix_ms = Some(reference.observed_at_unix_ms);
        }
        return;
    }

    let stable_symbol = definition.stable_symbol(&update.quote_mint);
    if let Some(symbol) = stable_symbol {
        update.price_usd = Some(primary.clone());
        update.reference_source_id = Some(format!(
            "{}:onchain:{symbol}-quote",
            definition.source_namespace
        ));
        update.reference_observed_at_unix_ms = Some(update.observed_at_unix_ms);
        return;
    }

    let Some(reference) = asset_usd_references.get(&update.quote_mint.to_ascii_lowercase()) else {
        return;
    };
    if !reference_is_stale(reference, now_unix_ms) {
        if let Ok(usd) = multiply_decimal(primary, &reference.value) {
            update.price_usd = Some(usd);
            update.reference_source_id = Some(reference.source_id.clone());
            update.reference_observed_at_unix_ms = Some(reference.observed_at_unix_ms);
        }
    }
}

fn apply_provider_context(
    update: &mut PriceUpdate,
    source_id: &str,
    provider_profile_id: Option<&str>,
) {
    update.source_id = source_id.to_owned();
    update.provider_profile_id = provider_profile_id.map(str::to_owned);
}

fn is_native_usd_reference_pool(pool_key: &crate::domain::PoolKey) -> bool {
    chain::by_chain_id(&pool_key.deployment_key.chain_id).is_some_and(|definition| {
        pool_key
            .pool_address()
            .eq_ignore_ascii_case(definition.native_usd_reference_pool)
            && pool_key.protocol_id() == definition.native_usd_reference_protocol
    })
}

fn is_singleton_protocol(protocol_id: &str) -> bool {
    matches!(
        protocol_id,
        uniswap_v4::PROTOCOL_ID
            | pancake_infinity_cl::PROTOCOL_ID
            | pancake_infinity_bin::PROTOCOL_ID
    )
}

fn reference_is_stale(reference: &ReferencePrice, now_unix_ms: i64) -> bool {
    now_unix_ms.saturating_sub(reference.observed_at_unix_ms) > REFERENCE_PRICE_STALE_AFTER_MS
}

fn references_disagree(primary: &DecimalValue, fallback: &DecimalValue) -> bool {
    let Ok(primary_coefficient) = BigUint::from_str(&primary.coefficient) else {
        return true;
    };
    let Ok(fallback_coefficient) = BigUint::from_str(&fallback.coefficient) else {
        return true;
    };
    if primary_coefficient == BigUint::from(0_u8) || fallback_coefficient == BigUint::from(0_u8) {
        return true;
    }
    let scale = primary.scale.max(fallback.scale);
    let normalized_primary =
        primary_coefficient * BigUint::from(10_u8).pow(scale.saturating_sub(primary.scale));
    let normalized_fallback =
        fallback_coefficient * BigUint::from(10_u8).pow(scale.saturating_sub(fallback.scale));
    let difference = if normalized_primary >= normalized_fallback {
        &normalized_primary - &normalized_fallback
    } else {
        &normalized_fallback - &normalized_primary
    };
    difference * BigUint::from(100_u8) > normalized_primary * BigUint::from(2_u8)
}

fn validate_selection(selection: &WatchedPoolSelection) -> Result<(), EvmMarketStateError> {
    let descriptor = &selection.descriptor;
    let deployment = &descriptor.pool_key.deployment_key;
    if descriptor.support_status != SupportStatus::Supported {
        return Err(EvmMarketStateError::UnsupportedPool(
            descriptor.pool_key.pool_address().to_owned(),
        ));
    }
    let Some(chain_definition) = chain::by_chain_id(&deployment.chain_id) else {
        return Err(EvmMarketStateError::UnsupportedDeployment);
    };
    if deployment.chain_namespace != chain::CHAIN_NAMESPACE
        || !matches!(
            deployment.protocol_id.as_str(),
            uniswap_v2::PROTOCOL_ID
                | pancake_v2::PROTOCOL_ID
                | uniswap_v3::PROTOCOL_ID
                | uniswap_v4::PROTOCOL_ID
                | pancake_v3::PROTOCOL_ID
                | pancake_infinity_cl::PROTOCOL_ID
                | pancake_infinity_bin::PROTOCOL_ID
                | aerodrome_classic::PROTOCOL_ID
                | aerodrome_slipstream::PROTOCOL_ID
                | curve::PROTOCOL_ID
                | fermi_swap::PROTOCOL_ID
                | pons_v2_curve::PROTOCOL_ID
        )
        || !(if is_singleton_protocol(&deployment.protocol_id) {
            uniswap_v4::is_pool_id(&descriptor.pool_key.pool_id)
        } else if deployment.protocol_id == fermi_swap::PROTOCOL_ID {
            uniswap_v4::is_pool_id(&descriptor.pool_key.pool_id)
                && descriptor
                    .pool_key
                    .pool_id
                    .eq_ignore_ascii_case(&fermi_swap::pair_id(
                        &deployment.contract_address,
                        &descriptor.base_mint,
                        &descriptor.quote_mint,
                    ))
        } else {
            uniswap_v2::is_address(&descriptor.pool_key.pool_id)
        })
        || !selection
            .selected_mint
            .eq_ignore_ascii_case(&descriptor.base_mint)
    {
        return Err(EvmMarketStateError::UnsupportedDeployment);
    }
    let supported_factory =
        chain_definition.supports_deployment(&deployment.protocol_id, &deployment.contract_address);
    if !supported_factory
        || !deployment
            .contract_address
            .eq_ignore_ascii_case(&descriptor.program_id)
    {
        return Err(EvmMarketStateError::UnsupportedDeployment);
    }
    let (Some(asset0), Some(asset1)) = (&descriptor.asset0, &descriptor.asset1) else {
        return Err(EvmMarketStateError::InvalidDescriptor);
    };
    if asset0.chain_namespace != chain::CHAIN_NAMESPACE
        || asset1.chain_namespace != chain::CHAIN_NAMESPACE
        || asset0.chain_id != deployment.chain_id
        || asset1.chain_id != deployment.chain_id
        || !uniswap_v2::is_address(&asset0.address)
        || !uniswap_v2::is_address(&asset1.address)
        || asset0.address.eq_ignore_ascii_case(&asset1.address)
    {
        return Err(EvmMarketStateError::InvalidDescriptor);
    }
    let has_base = asset0.address.eq_ignore_ascii_case(&descriptor.base_mint)
        || asset1.address.eq_ignore_ascii_case(&descriptor.base_mint);
    let has_quote = asset0.address.eq_ignore_ascii_case(&descriptor.quote_mint)
        || asset1.address.eq_ignore_ascii_case(&descriptor.quote_mint);
    if !has_base
        || !has_quote
        || descriptor
            .base_mint
            .eq_ignore_ascii_case(&descriptor.quote_mint)
    {
        return Err(EvmMarketStateError::InvalidDescriptor);
    }
    if matches!(
        deployment.protocol_id.as_str(),
        uniswap_v3::PROTOCOL_ID
            | uniswap_v4::PROTOCOL_ID
            | pancake_v3::PROTOCOL_ID
            | pancake_infinity_cl::PROTOCOL_ID
    ) && (descriptor.fee_tier.is_none()
        || descriptor.tick_spacing.is_none_or(|value| value <= 0))
    {
        return Err(EvmMarketStateError::InvalidDescriptor);
    }
    if deployment.protocol_id == pancake_infinity_bin::PROTOCOL_ID
        && (descriptor.pool_type != "binLiquidity"
            || descriptor.fee_tier.is_none()
            || descriptor.tick_spacing.is_some()
            || descriptor.bin_step.is_none_or(|value| value == 0)
            || descriptor
                .hook_address
                .as_ref()
                .is_none_or(|value| !uniswap_v2::is_address(value)))
    {
        return Err(EvmMarketStateError::InvalidDescriptor);
    }
    if deployment.protocol_id == pancake_infinity_cl::PROTOCOL_ID
        && (descriptor.pool_type != "concentratedLiquidity"
            || descriptor.bin_step.is_some()
            || descriptor
                .hook_address
                .as_ref()
                .is_none_or(|value| !uniswap_v2::is_address(value)))
    {
        return Err(EvmMarketStateError::InvalidDescriptor);
    }
    if deployment.protocol_id == pancake_v3::PROTOCOL_ID
        && (descriptor.pool_type != "concentratedLiquidity"
            || descriptor.bin_step.is_some()
            || descriptor.hook_address.is_some())
    {
        return Err(EvmMarketStateError::InvalidDescriptor);
    }
    if deployment.protocol_id == uniswap_v4::PROTOCOL_ID
        && descriptor.hook_address.as_ref().is_none_or(|value| {
            if !uniswap_v2::is_address(value) {
                return true;
            }
            let spot_only = descriptor.pricing_mode.as_deref() == Some("spotOnly");
            if deployment.chain_id == "8453" {
                return !value.eq_ignore_ascii_case(chain::BASE_MAINNET.native_asset_address)
                    || spot_only;
            }
            if uniswap_v4::hook_returns_swap_delta(value) {
                return !spot_only
                    || deployment.chain_id != "4663"
                    || !(value.eq_ignore_ascii_case("0x4e3468951d49f2eea976ed0d6e75ffcb44a9a544")
                        || value
                            .eq_ignore_ascii_case("0xe5e702641ea86f4ae6cc3cdaed2b886f976be044"));
            }
            spot_only
        })
    {
        return Err(EvmMarketStateError::InvalidDescriptor);
    }
    if deployment.protocol_id == aerodrome_classic::PROTOCOL_ID
        && (!matches!(descriptor.pool_type.as_str(), "volatile" | "stable")
            || descriptor.fee_tier.is_some()
            || descriptor.tick_spacing.is_some()
            || descriptor.hook_address.is_some())
    {
        return Err(EvmMarketStateError::InvalidDescriptor);
    }
    if deployment.protocol_id == aerodrome_slipstream::PROTOCOL_ID
        && (descriptor.pool_type != "concentratedLiquidity"
            || descriptor.fee_tier.is_some()
            || descriptor.tick_spacing.is_none_or(|tick_spacing| {
                !chain_definition
                    .supports_slipstream_tick_spacing(&deployment.contract_address, tick_spacing)
            })
            || descriptor.hook_address.is_some())
    {
        return Err(EvmMarketStateError::InvalidDescriptor);
    }
    if deployment.protocol_id == curve::PROTOCOL_ID
        && (descriptor.pool_type != "stableOrCryptoExecutedTrades"
            || descriptor.fee_tier.is_some()
            || descriptor.tick_spacing.is_some()
            || descriptor.bin_step.is_some()
            || descriptor.hook_address.is_some()
            || descriptor.protocol_accounts.len() != 2
            || curve_descriptor_indices(descriptor).is_none())
    {
        return Err(EvmMarketStateError::InvalidDescriptor);
    }
    if deployment.protocol_id == fermi_swap::PROTOCOL_ID
        && (descriptor.pool_type != "proprietaryInventoryExecutedTrades"
            || descriptor.fee_tier.is_some()
            || descriptor.tick_spacing.is_some()
            || descriptor.bin_step.is_some()
            || descriptor.hook_address.is_some()
            || !descriptor.protocol_accounts.is_empty())
    {
        return Err(EvmMarketStateError::InvalidDescriptor);
    }
    if deployment.protocol_id == pons_v2_curve::PROTOCOL_ID
        && (descriptor.pool_type != "constantProductBondingCurve"
            || descriptor.fee_tier.is_some()
            || descriptor.tick_spacing.is_some()
            || descriptor.bin_step.is_some()
            || descriptor.hook_address.is_some()
            || descriptor.pricing_mode.is_some()
            || !descriptor.protocol_accounts.is_empty())
    {
        return Err(EvmMarketStateError::InvalidDescriptor);
    }
    if deployment.protocol_id != uniswap_v4::PROTOCOL_ID && descriptor.pricing_mode.is_some() {
        return Err(EvmMarketStateError::InvalidDescriptor);
    }
    Ok(())
}

fn curve_descriptor_indices(descriptor: &crate::domain::PoolDescriptor) -> Option<(u32, u32)> {
    let base = descriptor
        .protocol_accounts
        .iter()
        .find(|account| {
            account.role == "baseCoin"
                && account.address.eq_ignore_ascii_case(&descriptor.base_mint)
        })?
        .index?;
    let quote = descriptor
        .protocol_accounts
        .iter()
        .find(|account| {
            account.role == "quoteCoin"
                && account.address.eq_ignore_ascii_case(&descriptor.quote_mint)
        })?
        .index?;
    (base != quote && base <= 7 && quote <= 7).then_some((base, quote))
}

fn ensure_supported_chain(chain_id: &str) -> Result<(), EvmMarketStateError> {
    if chain::by_chain_id(chain_id).is_some() {
        Ok(())
    } else {
        Err(EvmMarketStateError::WrongChain)
    }
}

fn finality_rank(finality: &EvmFinality) -> u8 {
    match finality {
        EvmFinality::Head => 0,
        EvmFinality::Safe => 1,
        EvmFinality::Finalized => 2,
    }
}

fn current_unix_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis().min(i64::MAX as u128) as i64)
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::domain::{AssetKey, DeploymentKey, PoolDescriptor, PoolKey, ProtocolAccount};
    use crate::evm::domain::SnapshotCall;

    const SHARED_POOL: &str = "0x1111111111111111111111111111111111111111";

    #[test]
    fn provider_recovery_and_snapshot_reset_are_isolated_by_chain() {
        let ethereum = selection(
            "1",
            uniswap_v2::MAINNET_FACTORY,
            chain::ETHEREUM_MAINNET.wrapped_native_asset_address,
            chain::ETHEREUM_MAINNET.stable_assets[0].address,
        );
        let base = selection(
            "8453",
            "0x8909dc15e40173ff4699343b6eb8132c65e18ec6",
            chain::BASE_MAINNET.wrapped_native_asset_address,
            chain::BASE_MAINNET.stable_assets[0].address,
        );
        let ethereum_key = ethereum.descriptor.pool_key.clone();
        let base_key = base.descriptor.pool_key.clone();
        let mut state = EvmMarketState::default();
        state.replace_watched_pools(vec![ethereum, base]).unwrap();
        state
            .set_provider_context("1", "eth-profile".to_owned(), "eth-source".to_owned())
            .unwrap();
        state
            .set_provider_context("8453", "base-profile".to_owned(), "base-source".to_owned())
            .unwrap();
        state.set_recovery_state("1", RecoveryState::Live).unwrap();
        state
            .set_recovery_state("8453", RecoveryState::Stale)
            .unwrap();

        let ethereum_update = state.apply_snapshot(snapshot(ethereum_key, 100)).unwrap();
        let base_update = state.apply_snapshot(snapshot(base_key, 200)).unwrap();
        assert_eq!(ethereum_update[0].source_id, "eth-source");
        assert_eq!(
            ethereum_update[0].provider_profile_id.as_deref(),
            Some("eth-profile")
        );
        assert_eq!(ethereum_update[0].recovery_state, RecoveryState::Live);
        assert_eq!(base_update[0].source_id, "base-source");
        assert_eq!(
            base_update[0].provider_profile_id.as_deref(),
            Some("base-profile")
        );
        assert_eq!(base_update[0].recovery_state, RecoveryState::Stale);

        state.reset_for_snapshot("8453").unwrap();
        let snapshots = state.snapshots();
        let ethereum_snapshot = snapshots
            .iter()
            .find(|update| update.pool_key.deployment_key.chain_id == "1")
            .unwrap();
        let base_snapshot = snapshots
            .iter()
            .find(|update| update.pool_key.deployment_key.chain_id == "8453")
            .unwrap();
        assert!(ethereum_snapshot.spot_price_quote.is_some());
        assert!(base_snapshot.spot_price_quote.is_none());
        assert_eq!(ethereum_snapshot.source_id, "eth-source");
        assert_eq!(base_snapshot.source_id, "base-source");
    }

    #[test]
    fn reference_disagreement_is_exact_and_withholds_stable_usd() {
        let decimal = |coefficient: &str, scale| DecimalValue {
            coefficient: coefficient.to_owned(),
            scale,
        };
        assert!(!references_disagree(
            &decimal("10000", 2),
            &decimal("102", 0)
        ));
        assert!(references_disagree(
            &decimal("10000", 2),
            &decimal("10201", 2)
        ));

        let mut update = PriceUpdate {
            pool_key: PoolKey {
                deployment_key: DeploymentKey {
                    chain_namespace: chain::CHAIN_NAMESPACE.to_owned(),
                    chain_id: "8453".to_owned(),
                    protocol_id: uniswap_v2::PROTOCOL_ID.to_owned(),
                    contract_address: chain::BASE_MAINNET.uniswap_v2_deployments[0].to_owned(),
                },
                pool_id: SHARED_POOL.to_owned(),
            },
            stream_epoch: 1,
            sequence: 1,
            last_trade_price_quote: Some(decimal("1", 0)),
            last_trade_event_id: None,
            spot_price_quote: None,
            price_sol: None,
            price_usd: None,
            price_native: None,
            quote_mint: chain::BASE_MAINNET.stable_assets[0].address.to_owned(),
            quote_asset: None,
            slot: 1,
            commitment: Commitment::Processed,
            chain_position: None,
            finality: None,
            source_id: String::new(),
            provider_profile_id: None,
            reference_source_id: None,
            reference_observed_at_unix_ms: None,
            observed_at_unix_ms: 10,
            chain_age_ms: None,
            stale: false,
            replaying: false,
            recovery_state: RecoveryState::Live,
        };
        apply_reference_prices(
            &mut update,
            &NativeUsdReferenceDecision {
                selected: None,
                disagreement_observed_at_unix_ms: Some(9),
            },
            &HashMap::new(),
            10,
        );
        assert!(update.price_usd.is_none());
        assert_eq!(
            update.reference_source_id.as_deref(),
            Some("base:reference-disagreement")
        );
        assert_eq!(update.reference_observed_at_unix_ms, Some(9));
    }

    #[test]
    fn aerodrome_classic_and_slipstream_route_through_chain_scoped_state() {
        let mut classic = selection(
            "8453",
            aerodrome_classic::BASE_FACTORY,
            chain::BASE_MAINNET.wrapped_native_asset_address,
            chain::BASE_MAINNET.stable_assets[0].address,
        );
        classic.descriptor.pool_key.deployment_key.protocol_id =
            aerodrome_classic::PROTOCOL_ID.to_owned();
        classic.descriptor.pool_key.pool_id =
            "0x2222222222222222222222222222222222222222".to_owned();
        classic.descriptor.pool_type = "stable".to_owned();
        classic.descriptor.program_id = aerodrome_classic::BASE_FACTORY.to_owned();

        let mut slipstream = selection(
            "8453",
            aerodrome_slipstream::BASE_INITIAL_FACTORY,
            chain::BASE_MAINNET.wrapped_native_asset_address,
            chain::BASE_MAINNET.stable_assets[0].address,
        );
        slipstream.descriptor.pool_key.deployment_key.protocol_id =
            aerodrome_slipstream::PROTOCOL_ID.to_owned();
        slipstream.descriptor.pool_key.pool_id =
            "0x3333333333333333333333333333333333333333".to_owned();
        slipstream.descriptor.pool_type = "concentratedLiquidity".to_owned();
        slipstream.descriptor.program_id = aerodrome_slipstream::BASE_INITIAL_FACTORY.to_owned();
        slipstream.descriptor.tick_spacing = Some(100);

        let mut unsupported_tick = slipstream.clone();
        unsupported_tick.descriptor.tick_spacing = Some(500);
        assert_eq!(
            EvmMarketState::validate_watched_pools(&[unsupported_tick]),
            Err(EvmMarketStateError::InvalidDescriptor)
        );

        let classic_key = classic.descriptor.pool_key.clone();
        let slipstream_key = slipstream.descriptor.pool_key.clone();
        let mut state = EvmMarketState::default();
        state
            .replace_watched_pools(vec![classic, slipstream])
            .unwrap();
        state
            .set_recovery_state("8453", RecoveryState::Live)
            .unwrap();

        let classic_snapshot = SnapshotResponse {
            request_id: "classic".to_owned(),
            pool_key: classic_key.clone(),
            block_number: 10,
            block_hash: "0x10".to_owned(),
            calls: vec![SnapshotCall {
                id: aerodrome_classic::RESERVES_CALL_ID.to_owned(),
                success: true,
                return_data: format!(
                    "0x{:064x}{:064x}{:064x}",
                    10_u128.pow(18),
                    10_u128.pow(6),
                    1
                ),
            }],
        };
        let classic_snapshot_update = state.apply_snapshot(classic_snapshot).unwrap();
        assert_eq!(
            classic_snapshot_update[0]
                .spot_price_quote
                .as_ref()
                .unwrap()
                .coefficient,
            "1000000000000000000"
        );
        let classic_swap_update = state
            .apply_log(LogUpdate {
                chain_id: "8453".to_owned(),
                connection_epoch: 1,
                address: classic_key.pool_id.clone(),
                topics: vec![
                    aerodrome_classic::SWAP_TOPIC.to_owned(),
                    "0xsender".to_owned(),
                    "0xto".to_owned(),
                ],
                data: format!(
                    "0x{:064x}{:064x}{:064x}{:064x}",
                    10_u128.pow(18),
                    0,
                    0,
                    2 * 10_u128.pow(6)
                ),
                block_number: 11,
                block_hash: "0x11".to_owned(),
                transaction_hash: "0xtx11".to_owned(),
                transaction_index: 1,
                log_index: 2,
                removed: false,
                observed_at_unix_ms: current_unix_ms(),
            })
            .unwrap();
        assert_eq!(
            classic_swap_update[0]
                .last_trade_price_quote
                .as_ref()
                .unwrap()
                .coefficient,
            "2000000000000000000"
        );

        let q96 = BigUint::from(1_u8) << 96;
        let slipstream_snapshot = SnapshotResponse {
            request_id: "slipstream".to_owned(),
            pool_key: slipstream_key.clone(),
            block_number: 12,
            block_hash: "0x12".to_owned(),
            calls: vec![
                SnapshotCall {
                    id: aerodrome_slipstream::SLOT0_CALL_ID.to_owned(),
                    success: true,
                    return_data: format!(
                        "0x{q96:064x}{:064x}{:064x}{:064x}{:064x}{:064x}",
                        0, 0, 0, 0, 1
                    ),
                },
                SnapshotCall {
                    id: aerodrome_slipstream::LIQUIDITY_CALL_ID.to_owned(),
                    success: true,
                    return_data: format!("0x{:064x}", 100),
                },
            ],
        };
        let slipstream_snapshot_update = state.apply_snapshot(slipstream_snapshot).unwrap();
        assert_eq!(
            slipstream_snapshot_update[0]
                .spot_price_quote
                .as_ref()
                .unwrap()
                .coefficient,
            "1000000000000000000000000000000"
        );
        let negative_quote = (BigUint::from(1_u8) << 256) - BigUint::from(2_000_000_u64);
        let slipstream_swap_update = state
            .apply_log(LogUpdate {
                chain_id: "8453".to_owned(),
                connection_epoch: 1,
                address: slipstream_key.pool_id,
                topics: vec![
                    aerodrome_slipstream::SWAP_TOPIC.to_owned(),
                    "0xsender".to_owned(),
                    "0xto".to_owned(),
                ],
                data: format!(
                    "0x{:064x}{negative_quote:064x}{q96:064x}{:064x}{:064x}",
                    10_u128.pow(18),
                    100,
                    0
                ),
                block_number: 13,
                block_hash: "0x13".to_owned(),
                transaction_hash: "0xtx13".to_owned(),
                transaction_index: 1,
                log_index: 2,
                removed: false,
                observed_at_unix_ms: current_unix_ms(),
            })
            .unwrap();
        assert_eq!(
            slipstream_swap_update[0]
                .last_trade_price_quote
                .as_ref()
                .unwrap()
                .coefficient,
            "2000000000000000000"
        );
    }

    #[test]
    fn curve_routes_only_the_validated_direct_coin_pair() {
        let base = chain::ETHEREUM_MAINNET.wrapped_native_asset_address;
        let quote = chain::ETHEREUM_MAINNET.stable_assets[0].address;
        let mut watched = selection("1", curve::MAINNET_ADDRESS_PROVIDER, base, quote);
        watched.descriptor.pool_key.deployment_key.protocol_id = curve::PROTOCOL_ID.to_owned();
        watched.descriptor.pool_key.pool_id =
            "0x2222222222222222222222222222222222222222".to_owned();
        watched.descriptor.pool_type = "stableOrCryptoExecutedTrades".to_owned();
        watched.descriptor.protocol_accounts = vec![
            ProtocolAccount {
                role: "baseCoin".to_owned(),
                address: base.to_owned(),
                index: Some(0),
            },
            ProtocolAccount {
                role: "quoteCoin".to_owned(),
                address: quote.to_owned(),
                index: Some(1),
            },
        ];
        let pool_address = watched.descriptor.pool_key.pool_id.clone();
        let mut state = EvmMarketState::default();
        state.replace_watched_pools(vec![watched]).unwrap();
        let log = |bought_id: u32, log_index| LogUpdate {
            chain_id: "1".to_owned(),
            connection_epoch: 1,
            address: pool_address.clone(),
            topics: vec![
                curve::TOKEN_EXCHANGE_SIGNED_TOPIC.to_owned(),
                format!("0x{:064x}", 1),
            ],
            data: format!(
                "0x{:064x}{:064x}{bought_id:064x}{:064x}",
                0, 2_000_000_000_000_000_000_u64, 4_000_000_000_u64
            ),
            block_number: 10,
            block_hash: "0x10".to_owned(),
            transaction_hash: format!("0xtx{log_index}"),
            transaction_index: 1,
            log_index,
            removed: false,
            observed_at_unix_ms: current_unix_ms(),
        };
        assert!(state.apply_log(log(2, 1)).unwrap().is_empty());
        let updates = state.apply_log(log(1, 2)).unwrap();
        assert_eq!(
            updates[0]
                .last_trade_price_quote
                .as_ref()
                .unwrap()
                .coefficient,
            "2000000000000000000000"
        );
        assert!(updates[0].spot_price_quote.is_none());
    }

    #[test]
    fn fermi_routes_one_contracts_execution_to_the_exact_token_pair() {
        let base = chain::ETHEREUM_MAINNET.wrapped_native_asset_address;
        let usdc = chain::ETHEREUM_MAINNET.stable_assets[0].address;
        let usdt = chain::ETHEREUM_MAINNET.stable_assets[1].address;
        let fermi_selection = |quote: &str| {
            let mut watched = selection("1", fermi_swap::CURRENT_SWAPPER, base, quote);
            watched.descriptor.pool_key.deployment_key.protocol_id =
                fermi_swap::PROTOCOL_ID.to_owned();
            watched.descriptor.pool_key.pool_id =
                fermi_swap::pair_id(fermi_swap::CURRENT_SWAPPER, base, quote);
            watched.descriptor.pool_type = "proprietaryInventoryExecutedTrades".to_owned();
            watched
        };
        let usdc_selection = fermi_selection(usdc);
        let expected_key = usdc_selection.descriptor.pool_key.clone();
        let mut state = EvmMarketState::default();
        state
            .replace_watched_pools(vec![usdc_selection, fermi_selection(usdt)])
            .unwrap();
        let topic_address = |address: &str| format!("0x{:0>64}", &address[2..]);
        let updates = state
            .apply_log(LogUpdate {
                chain_id: "1".to_owned(),
                connection_epoch: 1,
                address: fermi_swap::CURRENT_SWAPPER.to_owned(),
                topics: vec![
                    fermi_swap::SWAPPED_TOPIC.to_owned(),
                    topic_address("0x1111111111111111111111111111111111111111"),
                    topic_address(usdc),
                    topic_address(base),
                ],
                data: format!(
                    "0x{:064x}{:064x}{}",
                    4_000_000_000_u64,
                    2_000_000_000_000_000_000_u64,
                    &topic_address("0x2222222222222222222222222222222222222222")[2..]
                ),
                block_number: 20,
                block_hash: "0x20".to_owned(),
                transaction_hash: "0xtx20".to_owned(),
                transaction_index: 1,
                log_index: 2,
                removed: false,
                observed_at_unix_ms: current_unix_ms(),
            })
            .unwrap();
        assert_eq!(updates.len(), 1);
        assert_eq!(updates[0].pool_key, expected_key);
        assert_eq!(
            updates[0]
                .last_trade_price_quote
                .as_ref()
                .unwrap()
                .coefficient,
            "2000000000000000000000"
        );
        assert!(updates[0].spot_price_quote.is_none());
    }

    #[test]
    fn robinhood_stock_quote_reference_converts_exactly_to_usd() {
        let stock = "0x2222222222222222222222222222222222222222";
        let mut watched = selection(
            "4663",
            chain::ROBINHOOD_MAINNET.uniswap_v2_deployments[0],
            "0x1111111111111111111111111111111111111111",
            stock,
        );
        watched.descriptor.quote_decimals = 18;
        let pool_key = watched.descriptor.pool_key.clone();
        let mut state = EvmMarketState::default();
        state.replace_watched_pools(vec![watched]).unwrap();
        let initial = state
            .apply_snapshot(SnapshotResponse {
                request_id: "stock-quote".to_owned(),
                pool_key,
                block_number: 10,
                block_hash: "0x10".to_owned(),
                calls: vec![SnapshotCall {
                    id: uniswap_v2::RESERVES_CALL_ID.to_owned(),
                    success: true,
                    return_data: format!(
                        "0x{:064x}{:064x}{:064x}",
                        10_u128.pow(18),
                        2_u128 * 10_u128.pow(18),
                        1
                    ),
                }],
            })
            .unwrap();
        assert!(initial[0].price_usd.is_none());

        let observed_at = current_unix_ms();
        let updates = state
            .apply_asset_usd_reference(
                "4663",
                stock.to_owned(),
                DecimalValue {
                    coefficient: "100".to_owned(),
                    scale: 0,
                },
                "robinhood-stock-token-api:TEST".to_owned(),
                observed_at,
            )
            .unwrap();
        assert_eq!(
            updates[0].price_usd.as_ref().unwrap().coefficient,
            "200000000000000000000"
        );
        assert_eq!(updates[0].price_usd.as_ref().unwrap().scale, 18);
        assert_eq!(
            updates[0].reference_source_id.as_deref(),
            Some("robinhood-stock-token-api:TEST")
        );
        assert_eq!(updates[0].reference_observed_at_unix_ms, Some(observed_at));
    }

    #[test]
    fn pons_v2_curve_snapshot_trade_and_removed_log_share_reorg_state() {
        let base = "0x1111111111111111111111111111111111111111";
        let quote = chain::ROBINHOOD_MAINNET.wrapped_native_asset_address;
        let curve = "0x2222222222222222222222222222222222222222";
        let mut watched = selection("4663", pons_v2_curve::ROBINHOOD_FACTORY, base, quote);
        watched.descriptor.pool_key.deployment_key.protocol_id =
            pons_v2_curve::PROTOCOL_ID.to_owned();
        watched.descriptor.pool_key.pool_id = curve.to_owned();
        watched.descriptor.pool_type = "constantProductBondingCurve".to_owned();
        watched.descriptor.quote_decimals = 18;
        let pool_key = watched.descriptor.pool_key.clone();
        let mut state = EvmMarketState::default();
        state.replace_watched_pools(vec![watched]).unwrap();
        let snapshot_update = state
            .apply_snapshot(SnapshotResponse {
                request_id: "pons-v2-curve".to_owned(),
                pool_key,
                block_number: 10,
                block_hash: "0x10".to_owned(),
                calls: vec![SnapshotCall {
                    id: pons_v2_curve::RESERVES_CALL_ID.to_owned(),
                    success: true,
                    return_data: format!(
                        "0x{:064x}{:064x}",
                        2_u128 * 10_u128.pow(18),
                        10_u128.pow(18)
                    ),
                }],
            })
            .unwrap();
        assert_eq!(
            snapshot_update[0]
                .spot_price_quote
                .as_ref()
                .unwrap()
                .coefficient,
            "2000000000000000000"
        );

        let mut trade = LogUpdate {
            chain_id: "4663".to_owned(),
            connection_epoch: 1,
            address: curve.to_owned(),
            topics: vec![
                pons_v2_curve::BUY_TOPIC.to_owned(),
                "0xbuyer".to_owned(),
                "0xrecipient".to_owned(),
            ],
            data: format!(
                "0x{:064x}{:064x}{:064x}{:064x}",
                6_u128 * 10_u128.pow(18),
                2_u128 * 10_u128.pow(18),
                1,
                2
            ),
            block_number: 11,
            block_hash: "0x11".to_owned(),
            transaction_hash: "0xtx11".to_owned(),
            transaction_index: 1,
            log_index: 2,
            removed: false,
            observed_at_unix_ms: current_unix_ms(),
        };
        let trade_update = state.apply_log(trade.clone()).unwrap();
        assert_eq!(
            trade_update[0]
                .last_trade_price_quote
                .as_ref()
                .unwrap()
                .coefficient,
            "3000000000000000000"
        );
        trade.removed = true;
        let rollback_update = state.apply_log(trade).unwrap();
        assert!(rollback_update[0].last_trade_price_quote.is_none());
        assert_eq!(
            rollback_update[0]
                .spot_price_quote
                .as_ref()
                .unwrap()
                .coefficient,
            "2000000000000000000"
        );
    }

    #[test]
    fn robinhood_returned_delta_v4_uses_spot_only_pricing() {
        let base = "0x1111111111111111111111111111111111111111";
        let quote = chain::ROBINHOOD_MAINNET.wrapped_native_asset_address;
        let pool_id = format!("0x{:064x}", 7);
        let mut watched = selection(
            "4663",
            chain::ROBINHOOD_MAINNET.uniswap_v4_deployments[0],
            base,
            quote,
        );
        watched.descriptor.pool_key.deployment_key.protocol_id = uniswap_v4::PROTOCOL_ID.to_owned();
        watched.descriptor.pool_key.pool_id = pool_id.clone();
        watched.descriptor.pool_type = "singletonConcentratedLiquidity".to_owned();
        watched.descriptor.fee_tier = Some(500);
        watched.descriptor.tick_spacing = Some(10);
        watched.descriptor.hook_address =
            Some("0x4e3468951d49f2eea976ed0d6e75ffcb44a9a544".to_owned());
        watched.descriptor.pricing_mode = Some("spotOnly".to_owned());
        watched.descriptor.quote_decimals = 18;
        let mut state = EvmMarketState::default();
        state.replace_watched_pools(vec![watched]).unwrap();
        let negative_two = (BigUint::from(1_u8) << 256) - BigUint::from(2_u8);
        let q96 = BigUint::from(1_u8) << 96;
        let updates = state
            .apply_log(LogUpdate {
                chain_id: "4663".to_owned(),
                connection_epoch: 1,
                address: chain::ROBINHOOD_MAINNET.uniswap_v4_deployments[0].to_owned(),
                topics: vec![
                    uniswap_v4::SWAP_TOPIC.to_owned(),
                    pool_id,
                    format!("0x{:064x}", 8),
                ],
                data: format!(
                    "0x{:064x}{:064x}{:064x}{:064x}{:064x}{:064x}",
                    1, negative_two, q96, 100, 0, 500
                ),
                block_number: 12,
                block_hash: "0x12".to_owned(),
                transaction_hash: "0xtx12".to_owned(),
                transaction_index: 1,
                log_index: 2,
                removed: false,
                observed_at_unix_ms: current_unix_ms(),
            })
            .unwrap();
        assert!(updates[0].last_trade_price_quote.is_none());
        assert!(updates[0].last_trade_event_id.is_none());
        assert_eq!(
            updates[0].spot_price_quote.as_ref().unwrap().coefficient,
            "1000000000000000000"
        );
    }

    fn selection(chain_id: &str, factory: &str, base: &str, quote: &str) -> WatchedPoolSelection {
        let asset = |address: &str| AssetKey {
            chain_namespace: chain::CHAIN_NAMESPACE.to_owned(),
            chain_id: chain_id.to_owned(),
            address: address.to_owned(),
        };
        WatchedPoolSelection {
            selected_mint: base.to_owned(),
            descriptor: PoolDescriptor {
                pool_key: PoolKey {
                    deployment_key: DeploymentKey {
                        chain_namespace: chain::CHAIN_NAMESPACE.to_owned(),
                        chain_id: chain_id.to_owned(),
                        protocol_id: uniswap_v2::PROTOCOL_ID.to_owned(),
                        contract_address: factory.to_owned(),
                    },
                    pool_id: SHARED_POOL.to_owned(),
                },
                pool_type: "constantProduct".to_owned(),
                program_id: factory.to_owned(),
                base_mint: base.to_owned(),
                quote_mint: quote.to_owned(),
                base_decimals: 18,
                quote_decimals: 6,
                base_vault: None,
                quote_vault: None,
                protocol_accounts: Vec::new(),
                support_status: SupportStatus::Supported,
                support_reason: None,
                asset0: Some(asset(base)),
                asset1: Some(asset(quote)),
                validation_block: Some(1),
                validation_block_hash: Some("0x01".to_owned()),
                fee_tier: None,
                tick_spacing: None,
                bin_step: None,
                hook_address: None,
                pricing_mode: None,
            },
        }
    }

    fn snapshot(pool_key: PoolKey, block_number: u64) -> SnapshotResponse {
        SnapshotResponse {
            request_id: format!("snapshot-{block_number}"),
            pool_key,
            block_number,
            block_hash: format!("0x{block_number:x}"),
            calls: vec![SnapshotCall {
                id: uniswap_v2::RESERVES_CALL_ID.to_owned(),
                success: true,
                return_data: format!(
                    "0x{:064x}{:064x}{:064x}",
                    10_u128.pow(18),
                    2_000_000_u128,
                    1
                ),
            }],
        }
    }
}
