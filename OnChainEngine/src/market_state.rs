use crate::domain::{
    Commitment, DecimalValue, NormalizedSwap, PoolKey, PriceUpdate, RecoveryState, SupportStatus,
    TradeDirection, WatchedPoolSelection,
};
use crate::price::{
    constant_product_spot_price, dlmm_active_bin_spot_price, executed_trade_price,
    multiply_decimal, pump_curve_spot_price, pump_swap_spot_price, sqrt_price_x64_spot_price,
};
use crate::protocol::{
    manifest, meteora_damm_v1, meteora_damm_v2, meteora_dlmm, meteora_dynamic_vault,
    orca_whirlpool, pump, pump_swap, raydium_amm_v4, raydium_clmm, raydium_cpmm, spl_token,
};
use crate::raw::{RawAccountUpdate, RawInstruction, RawProgramData, RawTransactionUpdate};
use base64::engine::general_purpose::STANDARD as BASE64;
use base64::Engine as _;
use std::collections::{HashMap, VecDeque};
use std::time::{SystemTime, UNIX_EPOCH};
use thiserror::Error;

pub const PUMP_PROTOCOL_ID: &str = "pumpBondingCurve";
pub const PUMP_SWAP_PROTOCOL_ID: &str = "pumpSwap";
pub const RAYDIUM_AMM_V4_PROTOCOL_ID: &str = "raydiumAmmV4";
pub const RAYDIUM_CPMM_PROTOCOL_ID: &str = "raydiumCpmm";
pub const RAYDIUM_CLMM_PROTOCOL_ID: &str = "raydiumClmm";
pub const METEORA_DAMM_V1_PROTOCOL_ID: &str = "meteoraDammV1";
pub const METEORA_DAMM_V2_PROTOCOL_ID: &str = "meteoraDammV2";
pub const METEORA_DLMM_PROTOCOL_ID: &str = "meteoraDlmm";
pub const ORCA_WHIRLPOOL_PROTOCOL_ID: &str = "orcaWhirlpool";
pub const MANIFEST_PROTOCOL_ID: &str = "manifestOrderbook";
pub const TOKEN_PROGRAM_ID: &str = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
pub const TOKEN_2022_PROGRAM_ID: &str = "TokenzQdBNbLqP5VEhdkAS6EPFLC1PHnBqCXEpPxuEb";
pub const WRAPPED_SOL_MINT: &str = "So11111111111111111111111111111111111111112";
const SOLANA_USDC_MINT: &str = "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v";
const SOLANA_USDT_MINT: &str = "Es9vMFrzaCERmJfrF4H2FYD4KCoNkY11McCe8BenwNYB";
const EVENT_HISTORY_LIMIT: usize = 4096;
const REFERENCE_PRICE_STALE_AFTER_MS: i64 = 15_000;

#[derive(Debug, Error)]
pub enum MarketStateError {
    #[error("pool {0} is not supported")]
    UnsupportedPool(String),
    #[error("selected mint must be the base mint in the first on-chain slice")]
    UnsupportedOrientation,
    #[error("pool {0} uses an unknown protocol")]
    UnknownProtocol(String),
    #[error("base64 account or instruction data is invalid")]
    InvalidBase64,
    #[error("Pump account decode failed: {0}")]
    PumpAccount(#[from] pump::PumpDecodeError),
    #[error("PumpSwap account decode failed: {0}")]
    PumpSwapAccount(#[from] pump_swap::PumpSwapDecodeError),
    #[error("Raydium AMM v4 account decode failed: {0}")]
    RaydiumAmmV4Account(#[from] raydium_amm_v4::RaydiumAmmV4DecodeError),
    #[error("Raydium CPMM account decode failed: {0}")]
    RaydiumCpmmAccount(#[from] raydium_cpmm::RaydiumCpmmDecodeError),
    #[error("Raydium CLMM account decode failed: {0}")]
    RaydiumClmmAccount(#[from] raydium_clmm::RaydiumClmmDecodeError),
    #[error("Meteora DLMM account decode failed: {0}")]
    MeteoraDlmmAccount(#[from] meteora_dlmm::MeteoraDlmmDecodeError),
    #[error("Meteora DAMM v2 account decode failed: {0}")]
    MeteoraDammV2Account(#[from] meteora_damm_v2::MeteoraDammV2DecodeError),
    #[error("Meteora DAMM v1 account decode failed: {0}")]
    MeteoraDammV1Account(#[from] meteora_damm_v1::MeteoraDammV1DecodeError),
    #[error("Meteora Dynamic Vault account decode failed: {0}")]
    MeteoraDynamicVaultAccount(#[from] meteora_dynamic_vault::MeteoraDynamicVaultDecodeError),
    #[error("Manifest account or fill decode failed: {0}")]
    Manifest(#[from] manifest::ManifestDecodeError),
    #[error("Orca Whirlpool account decode failed: {0}")]
    OrcaWhirlpoolAccount(#[from] orca_whirlpool::OrcaWhirlpoolDecodeError),
    #[error("Pump trade decode failed: {0}")]
    PumpTrade(#[from] pump::PumpTradeDecodeError),
    #[error("PumpSwap trade decode failed: {0}")]
    PumpSwapTrade(#[from] pump_swap::PumpSwapTradeDecodeError),
    #[error("token account is truncated")]
    TruncatedTokenAccount,
    #[error("token account is owned by an unsupported token program")]
    UnsupportedTokenProgram,
    #[error("account state does not match the validated pool descriptor")]
    DescriptorMismatch,
    #[error("price calculation failed: {0}")]
    PriceMath(#[from] crate::price::PriceMathError),
    #[error("reference price source is empty")]
    EmptyReferenceSource,
    #[error("reference price timestamp is invalid")]
    InvalidReferenceTimestamp,
}

pub struct MarketState {
    stream_epoch: u64,
    pools: HashMap<PoolKey, PoolRuntime>,
    event_commitments: HashMap<String, Commitment>,
    event_order: VecDeque<String>,
    recovery_state: RecoveryState,
    sol_usd_reference: Option<ReferencePrice>,
}

impl Default for MarketState {
    fn default() -> Self {
        Self::new(current_epoch())
    }
}

impl MarketState {
    pub fn new(stream_epoch: u64) -> Self {
        Self {
            stream_epoch,
            pools: HashMap::new(),
            event_commitments: HashMap::new(),
            event_order: VecDeque::new(),
            recovery_state: RecoveryState::Connecting,
            sol_usd_reference: None,
        }
    }

    pub fn watched_pool_count(&self) -> usize {
        self.pools.len()
    }

    pub fn recovery_state(&self) -> &RecoveryState {
        &self.recovery_state
    }

    pub fn set_recovery_state(&mut self, recovery_state: RecoveryState) {
        self.recovery_state = recovery_state;
    }

    pub fn reset_for_snapshot(&mut self) {
        self.stream_epoch = current_epoch().max(self.stream_epoch.saturating_add(1));
        self.event_commitments.clear();
        self.event_order.clear();
        self.pools = std::mem::take(&mut self.pools)
            .into_values()
            .map(|runtime| {
                let selection = runtime.selection;
                (
                    selection.descriptor.pool_key.clone(),
                    PoolRuntime::new(selection),
                )
            })
            .collect();
    }

    pub fn apply_sol_usd_reference(
        &mut self,
        value: DecimalValue,
        source_id: String,
        observed_at_unix_ms: i64,
    ) -> Result<Vec<PriceUpdate>, MarketStateError> {
        if source_id.trim().is_empty() {
            return Err(MarketStateError::EmptyReferenceSource);
        }
        if observed_at_unix_ms <= 0 {
            return Err(MarketStateError::InvalidReferenceTimestamp);
        }
        multiply_decimal(
            &value,
            &DecimalValue {
                coefficient: "1".to_owned(),
                scale: 0,
            },
        )?;
        self.sol_usd_reference = Some(ReferencePrice {
            value,
            source_id,
            observed_at_unix_ms,
        });
        let reference = self.sol_usd_reference.clone();
        let stream_epoch = self.stream_epoch;
        let recovery_state = self.recovery_state.clone();
        let mut updates = Vec::new();
        for runtime in self
            .pools
            .values_mut()
            .filter(|runtime| runtime.has_primary_price())
        {
            let mut update = runtime.next_reference_update(stream_epoch, &recovery_state);
            apply_reference_prices(&mut update, reference.as_ref(), current_unix_ms());
            updates.push(update);
        }
        Ok(updates)
    }

    pub fn replace_watched_pools(
        &mut self,
        selections: Vec<WatchedPoolSelection>,
    ) -> Result<usize, MarketStateError> {
        Self::validate_watched_pools(&selections)?;
        let mut normalized = HashMap::new();
        for selection in selections {
            normalized.insert(selection.descriptor.pool_key.clone(), selection);
        }

        let mut old = std::mem::take(&mut self.pools);
        let mut replacement = HashMap::with_capacity(normalized.len());
        for (key, selection) in normalized {
            let runtime = if let Some(mut existing) = old.remove(&key) {
                existing.selection = selection;
                existing
            } else {
                PoolRuntime::new(selection)
            };
            replacement.insert(key, runtime);
        }
        self.pools = replacement;
        Ok(self.pools.len())
    }

    pub fn validate_watched_pools(
        selections: &[WatchedPoolSelection],
    ) -> Result<(), MarketStateError> {
        for selection in selections {
            validate_selection(selection)?;
        }
        Ok(())
    }

    pub fn apply_account_update(
        &mut self,
        update: RawAccountUpdate,
    ) -> Result<Vec<PriceUpdate>, MarketStateError> {
        let data = BASE64
            .decode(update.data_base64.as_bytes())
            .map_err(|_| MarketStateError::InvalidBase64)?;
        let mut updates = Vec::new();

        for runtime in self.pools.values_mut() {
            if !runtime.references_account(&update.pubkey) {
                continue;
            }
            if !runtime.accept_account_version(&update.pubkey, update.slot, update.write_version) {
                continue;
            }

            if runtime.apply_account(&update, &data)? {
                let price_update =
                    runtime.next_update(self.stream_epoch, &self.recovery_state, &update);
                updates.push(price_update);
            }
        }

        for update in &mut updates {
            apply_reference_prices(update, self.sol_usd_reference.as_ref(), current_unix_ms());
        }
        Ok(updates)
    }

    pub fn apply_account_snapshot(
        &mut self,
        accounts: Vec<RawAccountUpdate>,
    ) -> Result<Vec<PriceUpdate>, MarketStateError> {
        if accounts.is_empty() {
            return Ok(Vec::new());
        }
        if accounts
            .iter()
            .any(|account| account.slot != accounts[0].slot)
        {
            return Err(MarketStateError::DescriptorMismatch);
        }
        let decoded = accounts
            .iter()
            .map(|account| {
                BASE64
                    .decode(account.data_base64.as_bytes())
                    .map_err(|_| MarketStateError::InvalidBase64)
            })
            .collect::<Result<Vec<_>, _>>()?;
        let mut staged = Vec::new();
        let mut prices = Vec::new();
        for (key, runtime) in &self.pools {
            let descriptor = &runtime.selection.descriptor;
            let complete = std::iter::once(descriptor.pool_key.pool_address())
                .chain(descriptor.base_vault.as_deref())
                .chain(descriptor.quote_vault.as_deref())
                .chain(
                    descriptor
                        .protocol_accounts
                        .iter()
                        .map(|account| account.address.as_str()),
                )
                .all(|address| accounts.iter().any(|account| account.pubkey == address));
            if !complete {
                // A shared account can also belong to a pool in a later RPC batch.
                continue;
            }
            if !accounts
                .iter()
                .filter(|account| runtime.references_account(&account.pubkey))
                .any(|account| {
                    runtime
                        .account_versions
                        .get(&account.pubkey)
                        .is_none_or(|version| (account.slot, account.write_version) > *version)
                })
            {
                continue;
            }
            let mut next = PoolRuntime::new(runtime.selection.clone());
            next.sequence = runtime.sequence;
            let mut last = None;
            for (account, data) in accounts.iter().zip(&decoded) {
                if next.references_account(&account.pubkey)
                    && next.accept_account_version(
                        &account.pubkey,
                        account.slot,
                        account.write_version,
                    )
                {
                    next.apply_account(account, data)?;
                    last = Some(account);
                }
            }
            if let Some(last) = last.filter(|_| next.has_primary_price()) {
                let mut price = next.next_update(self.stream_epoch, &self.recovery_state, last);
                apply_reference_prices(
                    &mut price,
                    self.sol_usd_reference.as_ref(),
                    current_unix_ms(),
                );
                prices.push(price);
            }
            staged.push((key.clone(), next));
        }
        // Commit only after every affected pool has decoded successfully. No intermediate
        // price can combine a new base reserve with the previous quote reserve.
        self.pools.extend(staged);
        Ok(prices)
    }

    pub fn apply_transaction_update(
        &mut self,
        update: RawTransactionUpdate,
    ) -> Result<Vec<PriceUpdate>, MarketStateError> {
        if update.failed {
            return Ok(Vec::new());
        }

        let decoded_instruction_data = update
            .instructions
            .iter()
            .map(|instruction| {
                BASE64
                    .decode(instruction.data_base64.as_bytes())
                    .map_err(|_| MarketStateError::InvalidBase64)
            })
            .collect::<Result<Vec<_>, _>>()?;
        let mut updates = Vec::new();
        for (instruction, data) in update.instructions.iter().zip(&decoded_instruction_data) {
            if instruction.program_id == pump::PROGRAM_ID {
                if let Some(event) = pump::decode_trade_event(data)? {
                    let key = self.pools.iter().find_map(|(key, runtime)| {
                        let descriptor = &runtime.selection.descriptor;
                        (key.protocol_id() == PUMP_PROTOCOL_ID
                            && descriptor.base_mint == event.mint
                            && descriptor.quote_mint == WRAPPED_SOL_MINT)
                            .then(|| key.clone())
                    });
                    if let Some(key) = key {
                        self.apply_swap_candidate(
                            &update,
                            Some(instruction),
                            None,
                            key,
                            event.direction,
                            event.token_amount_raw,
                            event.quote_amount_raw,
                            "pumpTrade",
                            &mut updates,
                        )?;
                    }
                }
            } else if instruction.program_id == pump_swap::PROGRAM_ID {
                if let Some(event) = pump_swap::decode_trade_event(data)? {
                    let key = self
                        .pools
                        .keys()
                        .find(|key| {
                            key.protocol_id() == PUMP_SWAP_PROTOCOL_ID
                                && key.pool_address() == event.pool
                        })
                        .cloned();
                    if let Some(key) = key {
                        self.apply_swap_candidate(
                            &update,
                            Some(instruction),
                            None,
                            key,
                            event.direction,
                            event.base_amount_raw,
                            event.quote_amount_raw,
                            "pumpSwapTrade",
                            &mut updates,
                        )?;
                    }
                }
            }
        }

        let transfer_candidates = self
            .pools
            .iter()
            .flat_map(|(key, runtime)| {
                instruction_transfer_swaps(&update, &decoded_instruction_data, key, runtime)
            })
            .collect::<Vec<_>>();
        for (instruction_index, key, direction, base_amount_raw, quote_amount_raw, event_type) in
            transfer_candidates
        {
            self.apply_swap_candidate(
                &update,
                Some(&update.instructions[instruction_index]),
                None,
                key,
                direction,
                base_amount_raw,
                quote_amount_raw,
                event_type,
                &mut updates,
            )?;
        }

        for program_data in &update.program_data {
            if program_data.program_id == pump::PROGRAM_ID {
                let data = BASE64
                    .decode(program_data.data_base64.as_bytes())
                    .map_err(|_| MarketStateError::InvalidBase64)?;
                if let Some(event) = pump::decode_trade_event(&data)? {
                    let key = self.pools.iter().find_map(|(key, runtime)| {
                        let descriptor = &runtime.selection.descriptor;
                        (key.protocol_id() == PUMP_PROTOCOL_ID
                            && descriptor.base_mint == event.mint
                            && descriptor.quote_mint == WRAPPED_SOL_MINT)
                            .then(|| key.clone())
                    });
                    if let Some(key) = key {
                        self.apply_swap_candidate(
                            &update,
                            None,
                            Some(program_data),
                            key,
                            event.direction,
                            event.token_amount_raw,
                            event.quote_amount_raw,
                            "pumpTrade",
                            &mut updates,
                        )?;
                    }
                }
            } else if program_data.program_id == pump_swap::PROGRAM_ID {
                let data = BASE64
                    .decode(program_data.data_base64.as_bytes())
                    .map_err(|_| MarketStateError::InvalidBase64)?;
                if let Some(event) = pump_swap::decode_trade_event(&data)? {
                    let key = self
                        .pools
                        .keys()
                        .find(|key| {
                            key.protocol_id() == PUMP_SWAP_PROTOCOL_ID
                                && key.pool_address() == event.pool
                        })
                        .cloned();
                    if let Some(key) = key {
                        self.apply_swap_candidate(
                            &update,
                            None,
                            Some(program_data),
                            key,
                            event.direction,
                            event.base_amount_raw,
                            event.quote_amount_raw,
                            "pumpSwapTrade",
                            &mut updates,
                        )?;
                    }
                }
            } else if program_data.program_id == manifest::PROGRAM_ID {
                let data = BASE64
                    .decode(program_data.data_base64.as_bytes())
                    .map_err(|_| MarketStateError::InvalidBase64)?;
                if let Some(event) = manifest::decode_fill(&data)? {
                    let match_result = self.pools.iter().find_map(|(key, runtime)| {
                        if key.protocol_id() != MANIFEST_PROTOCOL_ID
                            || key.pool_address() != event.market
                        {
                            return None;
                        }
                        let descriptor = &runtime.selection.descriptor;
                        if descriptor.base_mint == event.base_mint
                            && descriptor.quote_mint == event.quote_mint
                        {
                            Some((
                                key.clone(),
                                event.direction.clone(),
                                event.base_amount_raw,
                                event.quote_amount_raw,
                            ))
                        } else if descriptor.base_mint == event.quote_mint
                            && descriptor.quote_mint == event.base_mint
                        {
                            Some((
                                key.clone(),
                                match event.direction {
                                    TradeDirection::Buy => TradeDirection::Sell,
                                    TradeDirection::Sell => TradeDirection::Buy,
                                },
                                event.quote_amount_raw,
                                event.base_amount_raw,
                            ))
                        } else {
                            None
                        }
                    });
                    if let Some((key, direction, base_amount_raw, quote_amount_raw)) = match_result
                    {
                        self.apply_swap_candidate(
                            &update,
                            None,
                            Some(program_data),
                            key,
                            direction,
                            base_amount_raw,
                            quote_amount_raw,
                            "manifestFill",
                            &mut updates,
                        )?;
                    }
                }
            }
        }

        for update in &mut updates {
            apply_reference_prices(update, self.sol_usd_reference.as_ref(), current_unix_ms());
        }

        Ok(updates)
    }

    pub fn snapshots(&self) -> Vec<PriceUpdate> {
        self.pools
            .values()
            .map(|runtime| {
                let mut update = runtime.snapshot(self.stream_epoch, &self.recovery_state);
                apply_reference_prices(
                    &mut update,
                    self.sol_usd_reference.as_ref(),
                    current_unix_ms(),
                );
                update
            })
            .collect()
    }

    #[allow(clippy::too_many_arguments)]
    fn apply_swap_candidate(
        &mut self,
        transaction: &RawTransactionUpdate,
        instruction: Option<&RawInstruction>,
        program_data: Option<&RawProgramData>,
        pool_key: PoolKey,
        direction: TradeDirection,
        base_amount_raw: u64,
        quote_amount_raw: u64,
        event_type: &str,
        output: &mut Vec<PriceUpdate>,
    ) -> Result<(), MarketStateError> {
        let location = if let Some(instruction) = instruction {
            format!(
                "{}:{}",
                instruction.outer_instruction_index,
                instruction
                    .inner_instruction_index
                    .map(|value| value.to_string())
                    .unwrap_or_else(|| "outer".to_owned())
            )
        } else {
            format!(
                "log:{}",
                program_data
                    .expect("swap candidate must have an instruction or program-data location")
                    .log_index
            )
        };
        let event_id = format!(
            "{}:{}:{}:{}",
            transaction.signature,
            location,
            event_type,
            pool_key.pool_address()
        );

        if !self.accept_event(&event_id, &transaction.commitment) {
            return Ok(());
        }

        let runtime = self
            .pools
            .get_mut(&pool_key)
            .expect("matched pool must remain present");
        let swap = NormalizedSwap {
            event_id,
            pool_key,
            slot: transaction.slot,
            signature: transaction.signature.clone(),
            outer_instruction_index: instruction.map(|value| value.outer_instruction_index),
            inner_instruction_index: instruction.and_then(|value| value.inner_instruction_index),
            log_index: program_data.map(|value| value.log_index),
            direction,
            base_amount_raw,
            quote_amount_raw,
            commitment: transaction.commitment.clone(),
            source_id: transaction.source_id.clone(),
            observed_at_unix_ms: transaction.observed_at_unix_ms,
        };
        runtime.apply_swap(&swap)?;
        output.push(runtime.next_swap_update(self.stream_epoch, &self.recovery_state, &swap));
        Ok(())
    }

    fn accept_event(&mut self, event_id: &str, commitment: &Commitment) -> bool {
        if let Some(previous) = self.event_commitments.get(event_id) {
            if commitment_rank(commitment) <= commitment_rank(previous) {
                return false;
            }
            self.event_commitments
                .insert(event_id.to_owned(), commitment.clone());
            return true;
        }

        self.event_commitments
            .insert(event_id.to_owned(), commitment.clone());
        self.event_order.push_back(event_id.to_owned());
        while self.event_order.len() > EVENT_HISTORY_LIMIT {
            if let Some(expired) = self.event_order.pop_front() {
                self.event_commitments.remove(&expired);
            }
        }
        true
    }
}

fn instruction_transfer_swaps(
    transaction: &RawTransactionUpdate,
    decoded_instruction_data: &[Vec<u8>],
    key: &PoolKey,
    runtime: &PoolRuntime,
) -> Vec<(usize, PoolKey, TradeDirection, u64, u64, &'static str)> {
    let descriptor = &runtime.selection.descriptor;
    let (event_type, is_swap_instruction): (&str, fn(&[u8]) -> bool) = match key.protocol_id() {
        RAYDIUM_AMM_V4_PROTOCOL_ID => ("raydiumAmmV4Swap", raydium_amm_v4::is_swap_instruction),
        RAYDIUM_CPMM_PROTOCOL_ID => ("raydiumCpmmSwap", raydium_cpmm::is_swap_instruction),
        RAYDIUM_CLMM_PROTOCOL_ID => ("raydiumClmmSwap", raydium_clmm::is_swap_instruction),
        METEORA_DAMM_V1_PROTOCOL_ID => ("meteoraDammV1Swap", meteora_damm_v1::is_swap_instruction),
        METEORA_DAMM_V2_PROTOCOL_ID => ("meteoraDammV2Swap", meteora_damm_v2::is_swap_instruction),
        METEORA_DLMM_PROTOCOL_ID => ("meteoraDlmmSwap", meteora_dlmm::is_swap_instruction),
        ORCA_WHIRLPOOL_PROTOCOL_ID => ("orcaWhirlpoolSwap", orca_whirlpool::is_swap_instruction),
        _ => return Vec::new(),
    };
    let Some(base_vault) = descriptor.base_vault.as_deref() else {
        return Vec::new();
    };
    let Some(quote_vault) = descriptor.quote_vault.as_deref() else {
        return Vec::new();
    };

    transaction
        .instructions
        .iter()
        .zip(decoded_instruction_data)
        .enumerate()
        .filter(|(_, (instruction, data))| {
            instruction.program_id == descriptor.program_id
                && instruction
                    .account_addresses
                    .iter()
                    .any(|address| address == key.pool_address())
                && is_swap_instruction(data)
        })
        .filter_map(|(instruction_index, _)| {
            let (base_delta, quote_delta) = pool_vault_transfer_deltas(
                transaction,
                decoded_instruction_data,
                instruction_index,
                base_vault,
                quote_vault,
            )?;
            let direction = match (base_delta.signum(), quote_delta.signum()) {
                (1, -1) => TradeDirection::Sell,
                (-1, 1) => TradeDirection::Buy,
                _ => return None,
            };
            let base_amount_raw = u64::try_from(base_delta.unsigned_abs()).ok()?;
            let quote_amount_raw = u64::try_from(quote_delta.unsigned_abs()).ok()?;
            Some((
                instruction_index,
                key.clone(),
                direction,
                base_amount_raw,
                quote_amount_raw,
                event_type,
            ))
        })
        .collect()
}

fn pool_vault_transfer_deltas(
    transaction: &RawTransactionUpdate,
    decoded_instruction_data: &[Vec<u8>],
    parent_index: usize,
    base_vault: &str,
    quote_vault: &str,
) -> Option<(i128, i128)> {
    let parent = transaction.instructions.get(parent_index)?;
    let parent_inner_index = parent.inner_instruction_index;
    let parent_depth = match parent_inner_index {
        Some(_) => Some(parent.stack_height?),
        None => None,
    };
    let boundary = parent_inner_index.and_then(|parent_inner_index| {
        transaction
            .instructions
            .iter()
            .filter(|instruction| {
                instruction.outer_instruction_index == parent.outer_instruction_index
                    && instruction
                        .inner_instruction_index
                        .is_some_and(|index| index > parent_inner_index)
                    && instruction
                        .stack_height
                        .is_none_or(|depth| depth <= parent_depth.expect("inner parent has depth"))
            })
            .filter_map(|instruction| instruction.inner_instruction_index)
            .min()
    });

    let mut base_delta = 0_i128;
    let mut quote_delta = 0_i128;
    for (instruction, data) in transaction
        .instructions
        .iter()
        .zip(decoded_instruction_data)
    {
        if instruction.outer_instruction_index != parent.outer_instruction_index
            || instruction.inner_instruction_index.is_none()
        {
            continue;
        }
        if let Some(parent_inner_index) = parent_inner_index {
            let child_inner_index = instruction.inner_instruction_index?;
            if child_inner_index <= parent_inner_index
                || boundary.is_some_and(|end| child_inner_index >= end)
                || instruction.stack_height? <= parent_depth?
            {
                continue;
            }
        }
        if instruction.program_id != spl_token::PROGRAM_ID
            && instruction.program_id != TOKEN_2022_PROGRAM_ID
        {
            continue;
        }
        if !matches!(data.first(), Some(3 | 12)) {
            continue;
        }
        let transfer = spl_token::decode_transfer(data, &instruction.account_addresses)?;
        apply_transfer_delta(&mut base_delta, base_vault, transfer);
        apply_transfer_delta(&mut quote_delta, quote_vault, transfer);
    }

    (base_delta != 0 && quote_delta != 0).then_some((base_delta, quote_delta))
}

fn apply_transfer_delta(delta: &mut i128, vault: &str, transfer: spl_token::Transfer<'_>) {
    if transfer.source == vault {
        *delta -= i128::from(transfer.amount);
    }
    if transfer.destination == vault {
        *delta += i128::from(transfer.amount);
    }
}

struct PoolRuntime {
    selection: WatchedPoolSelection,
    sequence: u64,
    account_versions: HashMap<String, (u64, u64)>,
    pump_curve: Option<pump::PumpBondingCurveState>,
    pump_swap: Option<pump_swap::PumpSwapPoolState>,
    raydium_amm_v4: Option<raydium_amm_v4::RaydiumAmmV4PoolState>,
    raydium_cpmm: Option<raydium_cpmm::RaydiumCpmmPoolState>,
    raydium_clmm: Option<raydium_clmm::RaydiumClmmPoolState>,
    meteora_damm_v1: Option<meteora_damm_v1::MeteoraDammV1PoolState>,
    meteora_damm_v2: Option<meteora_damm_v2::MeteoraDammV2PoolState>,
    meteora_dlmm: Option<meteora_dlmm::MeteoraDlmmPoolState>,
    orca_whirlpool: Option<orca_whirlpool::OrcaWhirlpoolState>,
    base_vault_amount: Option<u64>,
    quote_vault_amount: Option<u64>,
    base_dynamic_vault: Option<meteora_dynamic_vault::MeteoraDynamicVaultState>,
    quote_dynamic_vault: Option<meteora_dynamic_vault::MeteoraDynamicVaultState>,
    base_dynamic_vault_lp_amount: Option<u64>,
    quote_dynamic_vault_lp_amount: Option<u64>,
    base_dynamic_vault_lp_supply: Option<u64>,
    quote_dynamic_vault_lp_supply: Option<u64>,
    spot_price: Option<DecimalValue>,
    last_trade_price: Option<DecimalValue>,
    last_trade_event_id: Option<String>,
    last_slot: u64,
    last_commitment: Commitment,
    last_source_id: String,
    last_observed_at_unix_ms: i64,
}

#[derive(Clone)]
struct ReferencePrice {
    value: DecimalValue,
    source_id: String,
    observed_at_unix_ms: i64,
}

impl PoolRuntime {
    fn new(selection: WatchedPoolSelection) -> Self {
        Self {
            selection,
            sequence: 0,
            account_versions: HashMap::new(),
            pump_curve: None,
            pump_swap: None,
            raydium_amm_v4: None,
            raydium_cpmm: None,
            raydium_clmm: None,
            meteora_damm_v1: None,
            meteora_damm_v2: None,
            meteora_dlmm: None,
            orca_whirlpool: None,
            base_vault_amount: None,
            quote_vault_amount: None,
            base_dynamic_vault: None,
            quote_dynamic_vault: None,
            base_dynamic_vault_lp_amount: None,
            quote_dynamic_vault_lp_amount: None,
            base_dynamic_vault_lp_supply: None,
            quote_dynamic_vault_lp_supply: None,
            spot_price: None,
            last_trade_price: None,
            last_trade_event_id: None,
            last_slot: 0,
            last_commitment: Commitment::Processed,
            last_source_id: String::new(),
            last_observed_at_unix_ms: 0,
        }
    }

    fn references_account(&self, pubkey: &str) -> bool {
        let descriptor = &self.selection.descriptor;
        descriptor.pool_key.pool_address() == pubkey
            || descriptor.base_vault.as_deref() == Some(pubkey)
            || descriptor.quote_vault.as_deref() == Some(pubkey)
            || descriptor
                .protocol_accounts
                .iter()
                .any(|account| account.address == pubkey)
    }

    fn accept_account_version(&mut self, pubkey: &str, slot: u64, write_version: u64) -> bool {
        let incoming = (slot, write_version);
        if self
            .account_versions
            .get(pubkey)
            .is_some_and(|current| incoming <= *current)
        {
            return false;
        }
        self.account_versions.insert(pubkey.to_owned(), incoming);
        true
    }

    fn apply_account(
        &mut self,
        update: &RawAccountUpdate,
        data: &[u8],
    ) -> Result<bool, MarketStateError> {
        let descriptor = &self.selection.descriptor;
        if update.pubkey == descriptor.pool_key.pool_address() {
            if update.owner_program != descriptor.program_id {
                return Err(MarketStateError::DescriptorMismatch);
            }
            match descriptor.pool_key.protocol_id() {
                PUMP_PROTOCOL_ID => {
                    let state = pump::decode_bonding_curve(&update.owner_program, data)?;
                    if state.quote_mint != descriptor.quote_mint {
                        return Err(MarketStateError::DescriptorMismatch);
                    }
                    self.spot_price = Some(pump_curve_spot_price(
                        state.virtual_quote_reserves,
                        state.virtual_token_reserves,
                        descriptor.base_decimals,
                        descriptor.quote_decimals,
                    )?);
                    self.pump_curve = Some(state);
                }
                PUMP_SWAP_PROTOCOL_ID => {
                    let state = pump_swap::decode_pool(&update.owner_program, data)?;
                    if state.base_mint != descriptor.base_mint
                        || state.quote_mint != descriptor.quote_mint
                        || Some(state.pool_base_token_account.as_str())
                            != descriptor.base_vault.as_deref()
                        || Some(state.pool_quote_token_account.as_str())
                            != descriptor.quote_vault.as_deref()
                    {
                        return Err(MarketStateError::DescriptorMismatch);
                    }
                    self.pump_swap = Some(state);
                    self.recalculate_vault_spot()?;
                }
                RAYDIUM_AMM_V4_PROTOCOL_ID => {
                    let state = raydium_amm_v4::decode_pool(&update.owner_program, data)?;
                    if !matches_oriented_pair(
                        descriptor,
                        &state.coin_mint,
                        &state.pc_mint,
                        &state.coin_vault,
                        &state.pc_vault,
                    ) {
                        return Err(MarketStateError::DescriptorMismatch);
                    }
                    self.raydium_amm_v4 = Some(state);
                    self.recalculate_vault_spot()?;
                }
                RAYDIUM_CPMM_PROTOCOL_ID => {
                    let state = raydium_cpmm::decode_pool(&update.owner_program, data)?;
                    if !matches_oriented_pair(
                        descriptor,
                        &state.token_0_mint,
                        &state.token_1_mint,
                        &state.token_0_vault,
                        &state.token_1_vault,
                    ) {
                        return Err(MarketStateError::DescriptorMismatch);
                    }
                    self.raydium_cpmm = Some(state);
                    self.recalculate_vault_spot()?;
                }
                RAYDIUM_CLMM_PROTOCOL_ID => {
                    let state = raydium_clmm::decode_pool(&update.owner_program, data)?;
                    if !matches_oriented_pair(
                        descriptor,
                        &state.token_0_mint,
                        &state.token_1_mint,
                        &state.token_0_vault,
                        &state.token_1_vault,
                    ) {
                        return Err(MarketStateError::DescriptorMismatch);
                    }
                    self.spot_price = Some(sqrt_price_x64_spot_price(
                        state.sqrt_price_x64,
                        state.token_0_mint == descriptor.base_mint,
                        descriptor.base_decimals,
                        descriptor.quote_decimals,
                    )?);
                    self.raydium_clmm = Some(state);
                }
                METEORA_DAMM_V1_PROTOCOL_ID => {
                    let state = meteora_damm_v1::decode_pool(&update.owner_program, data)?;
                    if state.curve_type != meteora_damm_v1::CurveType::ConstantProduct
                        || !state.enabled
                        || !matches_damm_v1_pool(descriptor, &state)
                    {
                        return Err(MarketStateError::DescriptorMismatch);
                    }
                    self.meteora_damm_v1 = Some(state);
                    self.recalculate_vault_spot()?;
                }
                METEORA_DAMM_V2_PROTOCOL_ID => {
                    let state = meteora_damm_v2::decode_pool(&update.owner_program, data)?;
                    if !matches_oriented_pair(
                        descriptor,
                        &state.token_a_mint,
                        &state.token_b_mint,
                        &state.token_a_vault,
                        &state.token_b_vault,
                    ) {
                        return Err(MarketStateError::DescriptorMismatch);
                    }
                    self.spot_price = Some(sqrt_price_x64_spot_price(
                        state.sqrt_price_x64,
                        state.token_a_mint == descriptor.base_mint,
                        descriptor.base_decimals,
                        descriptor.quote_decimals,
                    )?);
                    self.meteora_damm_v2 = Some(state);
                }
                METEORA_DLMM_PROTOCOL_ID => {
                    let state = meteora_dlmm::decode_pool(&update.owner_program, data)?;
                    if !matches_oriented_pair(
                        descriptor,
                        &state.token_x_mint,
                        &state.token_y_mint,
                        &state.reserve_x,
                        &state.reserve_y,
                    ) {
                        return Err(MarketStateError::DescriptorMismatch);
                    }
                    self.spot_price = Some(dlmm_active_bin_spot_price(
                        state.active_id,
                        state.bin_step,
                        state.token_x_mint == descriptor.base_mint,
                        descriptor.base_decimals,
                        descriptor.quote_decimals,
                    )?);
                    self.meteora_dlmm = Some(state);
                }
                ORCA_WHIRLPOOL_PROTOCOL_ID => {
                    let state = orca_whirlpool::decode_pool(&update.owner_program, data)?;
                    if !matches_oriented_pair(
                        descriptor,
                        &state.token_mint_a,
                        &state.token_mint_b,
                        &state.token_vault_a,
                        &state.token_vault_b,
                    ) {
                        return Err(MarketStateError::DescriptorMismatch);
                    }
                    self.spot_price = Some(sqrt_price_x64_spot_price(
                        state.sqrt_price_x64,
                        state.token_mint_a == descriptor.base_mint,
                        descriptor.base_decimals,
                        descriptor.quote_decimals,
                    )?);
                    self.orca_whirlpool = Some(state);
                }
                MANIFEST_PROTOCOL_ID => {
                    let state = manifest::decode_market(&update.owner_program, data)?;
                    if !matches_oriented_pair(
                        descriptor,
                        &state.base_mint,
                        &state.quote_mint,
                        &state.base_vault,
                        &state.quote_vault,
                    ) {
                        return Err(MarketStateError::DescriptorMismatch);
                    }
                }
                protocol => return Err(MarketStateError::UnknownProtocol(protocol.to_owned())),
            }
        } else if descriptor.base_vault.as_deref() == Some(update.pubkey.as_str()) {
            let token = decode_token_account(&update.owner_program, data)?;
            if token.mint != descriptor.base_mint {
                return Err(MarketStateError::DescriptorMismatch);
            }
            self.base_vault_amount = Some(token.amount);
            self.recalculate_vault_spot()?;
        } else if descriptor.quote_vault.as_deref() == Some(update.pubkey.as_str()) {
            let token = decode_token_account(&update.owner_program, data)?;
            if token.mint != descriptor.quote_mint {
                return Err(MarketStateError::DescriptorMismatch);
            }
            self.quote_vault_amount = Some(token.amount);
            self.recalculate_vault_spot()?;
        } else if let Some(role) = protocol_account_role(descriptor, &update.pubkey) {
            match role {
                "baseVaultState" => {
                    let vault = meteora_dynamic_vault::decode_vault(&update.owner_program, data)?;
                    if !matches_dynamic_vault(descriptor, &vault, true) {
                        return Err(MarketStateError::DescriptorMismatch);
                    }
                    self.base_dynamic_vault = Some(vault);
                }
                "quoteVaultState" => {
                    let vault = meteora_dynamic_vault::decode_vault(&update.owner_program, data)?;
                    if !matches_dynamic_vault(descriptor, &vault, false) {
                        return Err(MarketStateError::DescriptorMismatch);
                    }
                    self.quote_dynamic_vault = Some(vault);
                }
                "baseVaultLp" => {
                    let token = decode_token_account(&update.owner_program, data)?;
                    if Some(token.mint.as_str())
                        != protocol_account_address(descriptor, "baseVaultLpMint")
                    {
                        return Err(MarketStateError::DescriptorMismatch);
                    }
                    self.base_dynamic_vault_lp_amount = Some(token.amount);
                }
                "quoteVaultLp" => {
                    let token = decode_token_account(&update.owner_program, data)?;
                    if Some(token.mint.as_str())
                        != protocol_account_address(descriptor, "quoteVaultLpMint")
                    {
                        return Err(MarketStateError::DescriptorMismatch);
                    }
                    self.quote_dynamic_vault_lp_amount = Some(token.amount);
                }
                "baseVaultLpMint" => {
                    self.base_dynamic_vault_lp_supply =
                        Some(decode_mint_supply(&update.owner_program, data)?);
                }
                "quoteVaultLpMint" => {
                    self.quote_dynamic_vault_lp_supply =
                        Some(decode_mint_supply(&update.owner_program, data)?);
                }
                _ => return Err(MarketStateError::DescriptorMismatch),
            }
            self.recalculate_vault_spot()?;
        }

        self.last_slot = update.slot;
        self.last_commitment = update.commitment.clone();
        self.last_source_id.clone_from(&update.source_id);
        self.last_observed_at_unix_ms = update.observed_at_unix_ms;
        Ok(self.spot_price.is_some())
    }

    fn recalculate_vault_spot(&mut self) -> Result<(), MarketStateError> {
        self.recalculate_pump_swap_spot()?;
        self.recalculate_raydium_amm_v4_spot()?;
        self.recalculate_raydium_cpmm_spot()?;
        self.recalculate_meteora_damm_v1_spot()?;
        Ok(())
    }

    fn recalculate_meteora_damm_v1_spot(&mut self) -> Result<(), MarketStateError> {
        if self.meteora_damm_v1.is_none() {
            return Ok(());
        }
        let (
            Some(base_vault),
            Some(quote_vault),
            Some(base_lp),
            Some(quote_lp),
            Some(base_supply),
            Some(quote_supply),
        ) = (
            self.base_dynamic_vault.as_ref(),
            self.quote_dynamic_vault.as_ref(),
            self.base_dynamic_vault_lp_amount,
            self.quote_dynamic_vault_lp_amount,
            self.base_dynamic_vault_lp_supply,
            self.quote_dynamic_vault_lp_supply,
        )
        else {
            return Ok(());
        };
        if base_supply == 0 || quote_supply == 0 {
            return Ok(());
        }
        let current_time = u64::try_from(current_unix_ms() / 1000).unwrap_or(0);
        let (Some(base_unlocked), Some(quote_unlocked)) = (
            base_vault.unlocked_amount(current_time),
            quote_vault.unlocked_amount(current_time),
        ) else {
            return Ok(());
        };
        let base_reserve =
            (u128::from(base_lp) * u128::from(base_unlocked)) / u128::from(base_supply);
        let quote_reserve =
            (u128::from(quote_lp) * u128::from(quote_unlocked)) / u128::from(quote_supply);
        let (Ok(base_reserve), Ok(quote_reserve)) =
            (u64::try_from(base_reserve), u64::try_from(quote_reserve))
        else {
            return Ok(());
        };
        self.spot_price = Some(constant_product_spot_price(
            quote_reserve,
            0,
            0,
            0,
            base_reserve,
            0,
            0,
            0,
            self.selection.descriptor.base_decimals,
            self.selection.descriptor.quote_decimals,
        )?);
        Ok(())
    }

    fn recalculate_pump_swap_spot(&mut self) -> Result<(), MarketStateError> {
        let (Some(state), Some(base), Some(quote)) = (
            self.pump_swap.as_ref(),
            self.base_vault_amount,
            self.quote_vault_amount,
        ) else {
            return Ok(());
        };
        let descriptor = &self.selection.descriptor;
        self.spot_price = Some(pump_swap_spot_price(
            quote,
            state.virtual_quote_reserves,
            base,
            descriptor.base_decimals,
            descriptor.quote_decimals,
        )?);
        Ok(())
    }

    fn recalculate_raydium_cpmm_spot(&mut self) -> Result<(), MarketStateError> {
        let (Some(state), Some(base), Some(quote)) = (
            self.raydium_cpmm.as_ref(),
            self.base_vault_amount,
            self.quote_vault_amount,
        ) else {
            return Ok(());
        };
        let descriptor = &self.selection.descriptor;
        let selected_is_token_0 = state.token_0_mint == descriptor.base_mint;
        let (base_protocol, base_fund, base_creator, quote_protocol, quote_fund, quote_creator) =
            if selected_is_token_0 {
                (
                    state.protocol_fees_token_0,
                    state.fund_fees_token_0,
                    state.creator_fees_token_0,
                    state.protocol_fees_token_1,
                    state.fund_fees_token_1,
                    state.creator_fees_token_1,
                )
            } else {
                (
                    state.protocol_fees_token_1,
                    state.fund_fees_token_1,
                    state.creator_fees_token_1,
                    state.protocol_fees_token_0,
                    state.fund_fees_token_0,
                    state.creator_fees_token_0,
                )
            };
        self.spot_price = Some(constant_product_spot_price(
            quote,
            quote_protocol,
            quote_fund,
            quote_creator,
            base,
            base_protocol,
            base_fund,
            base_creator,
            descriptor.base_decimals,
            descriptor.quote_decimals,
        )?);
        Ok(())
    }

    fn recalculate_raydium_amm_v4_spot(&mut self) -> Result<(), MarketStateError> {
        let (Some(state), Some(base), Some(quote)) = (
            self.raydium_amm_v4.as_ref(),
            self.base_vault_amount,
            self.quote_vault_amount,
        ) else {
            return Ok(());
        };
        let descriptor = &self.selection.descriptor;
        let selected_is_coin = state.coin_mint == descriptor.base_mint;
        let (base_pending_pnl, quote_pending_pnl) = if selected_is_coin {
            (state.need_take_pnl_coin, state.need_take_pnl_pc)
        } else {
            (state.need_take_pnl_pc, state.need_take_pnl_coin)
        };
        self.spot_price = Some(constant_product_spot_price(
            quote,
            quote_pending_pnl,
            0,
            0,
            base,
            base_pending_pnl,
            0,
            0,
            descriptor.base_decimals,
            descriptor.quote_decimals,
        )?);
        Ok(())
    }

    fn apply_swap(&mut self, swap: &NormalizedSwap) -> Result<(), MarketStateError> {
        let descriptor = &self.selection.descriptor;
        self.last_trade_price = Some(executed_trade_price(
            swap.quote_amount_raw,
            swap.base_amount_raw,
            descriptor.base_decimals,
            descriptor.quote_decimals,
        )?);
        self.last_trade_event_id = Some(swap.event_id.clone());
        self.last_slot = swap.slot;
        self.last_commitment = swap.commitment.clone();
        self.last_source_id.clone_from(&swap.source_id);
        self.last_observed_at_unix_ms = swap.observed_at_unix_ms;
        Ok(())
    }

    fn has_primary_price(&self) -> bool {
        self.last_trade_price.is_some() || self.spot_price.is_some()
    }

    fn next_update(
        &mut self,
        stream_epoch: u64,
        recovery_state: &RecoveryState,
        update: &RawAccountUpdate,
    ) -> PriceUpdate {
        self.sequence = self.sequence.saturating_add(1);
        self.build_update(
            stream_epoch,
            self.sequence,
            update.slot,
            update.commitment.clone(),
            update.source_id.clone(),
            update.observed_at_unix_ms,
            recovery_state,
        )
    }

    fn next_swap_update(
        &mut self,
        stream_epoch: u64,
        recovery_state: &RecoveryState,
        swap: &NormalizedSwap,
    ) -> PriceUpdate {
        self.sequence = self.sequence.saturating_add(1);
        self.build_update(
            stream_epoch,
            self.sequence,
            swap.slot,
            swap.commitment.clone(),
            swap.source_id.clone(),
            swap.observed_at_unix_ms,
            recovery_state,
        )
    }

    fn next_reference_update(
        &mut self,
        stream_epoch: u64,
        recovery_state: &RecoveryState,
    ) -> PriceUpdate {
        self.sequence = self.sequence.saturating_add(1);
        self.build_update(
            stream_epoch,
            self.sequence,
            self.last_slot,
            self.last_commitment.clone(),
            self.last_source_id.clone(),
            self.last_observed_at_unix_ms,
            recovery_state,
        )
    }

    fn snapshot(&self, stream_epoch: u64, recovery_state: &RecoveryState) -> PriceUpdate {
        self.build_update(
            stream_epoch,
            self.sequence,
            self.last_slot,
            self.last_commitment.clone(),
            self.last_source_id.clone(),
            self.last_observed_at_unix_ms,
            recovery_state,
        )
    }

    #[allow(clippy::too_many_arguments)]
    fn build_update(
        &self,
        stream_epoch: u64,
        sequence: u64,
        slot: u64,
        commitment: Commitment,
        source_id: String,
        observed_at_unix_ms: i64,
        recovery_state: &RecoveryState,
    ) -> PriceUpdate {
        PriceUpdate {
            pool_key: self.selection.descriptor.pool_key.clone(),
            stream_epoch,
            sequence,
            last_trade_price_quote: self.last_trade_price.clone(),
            last_trade_event_id: self.last_trade_event_id.clone(),
            spot_price_quote: self.spot_price.clone(),
            price_sol: None,
            price_usd: None,
            price_native: None,
            quote_mint: self.selection.descriptor.quote_mint.clone(),
            quote_asset: None,
            slot,
            commitment,
            chain_position: None,
            finality: None,
            source_id,
            provider_profile_id: None,
            reference_source_id: None,
            reference_observed_at_unix_ms: None,
            observed_at_unix_ms,
            chain_age_ms: None,
            stale: matches!(recovery_state, RecoveryState::Stale),
            replaying: matches!(recovery_state, RecoveryState::Replaying),
            recovery_state: recovery_state.clone(),
        }
    }
}

fn apply_reference_prices(
    update: &mut PriceUpdate,
    reference: Option<&ReferencePrice>,
    now_unix_ms: i64,
) {
    let Some(primary) = update
        .last_trade_price_quote
        .as_ref()
        .or(update.spot_price_quote.as_ref())
    else {
        return;
    };
    let stable_symbol = match update.quote_mint.as_str() {
        SOLANA_USDC_MINT => Some("USDC"),
        SOLANA_USDT_MINT => Some("USDT"),
        _ => None,
    };
    if let Some(symbol) = stable_symbol {
        update.price_usd = Some(primary.clone());
        update.reference_source_id = Some(format!("solana:onchain:{symbol}-quote"));
        update.reference_observed_at_unix_ms = Some(update.observed_at_unix_ms);
        return;
    }
    if update.quote_mint != WRAPPED_SOL_MINT {
        return;
    }
    update.price_sol = Some(primary.clone());
    let Some(reference) = reference else {
        return;
    };
    if now_unix_ms.saturating_sub(reference.observed_at_unix_ms) > REFERENCE_PRICE_STALE_AFTER_MS {
        return;
    }
    if let Ok(usd) = multiply_decimal(primary, &reference.value) {
        update.price_usd = Some(usd);
        update.reference_source_id = Some(reference.source_id.clone());
        update.reference_observed_at_unix_ms = Some(reference.observed_at_unix_ms);
    }
}

struct TokenAccountState {
    mint: String,
    amount: u64,
}

fn decode_token_account(
    owner_program: &str,
    data: &[u8],
) -> Result<TokenAccountState, MarketStateError> {
    if owner_program != TOKEN_PROGRAM_ID && owner_program != TOKEN_2022_PROGRAM_ID {
        return Err(MarketStateError::UnsupportedTokenProgram);
    }
    let mint: [u8; 32] = data
        .get(0..32)
        .ok_or(MarketStateError::TruncatedTokenAccount)?
        .try_into()
        .map_err(|_| MarketStateError::TruncatedTokenAccount)?;
    let amount = u64::from_le_bytes(
        data.get(64..72)
            .ok_or(MarketStateError::TruncatedTokenAccount)?
            .try_into()
            .map_err(|_| MarketStateError::TruncatedTokenAccount)?,
    );
    Ok(TokenAccountState {
        mint: bs58::encode(mint).into_string(),
        amount,
    })
}

fn decode_mint_supply(owner_program: &str, data: &[u8]) -> Result<u64, MarketStateError> {
    if owner_program != TOKEN_PROGRAM_ID {
        return Err(MarketStateError::UnsupportedTokenProgram);
    }
    let supply = data
        .get(36..44)
        .ok_or(MarketStateError::TruncatedTokenAccount)?
        .try_into()
        .map(u64::from_le_bytes)
        .map_err(|_| MarketStateError::TruncatedTokenAccount)?;
    if data.get(45).copied() != Some(1) {
        return Err(MarketStateError::TruncatedTokenAccount);
    }
    Ok(supply)
}

fn validate_selection(selection: &WatchedPoolSelection) -> Result<(), MarketStateError> {
    let descriptor = &selection.descriptor;
    if descriptor.support_status != SupportStatus::Supported {
        return Err(MarketStateError::UnsupportedPool(
            descriptor.pool_key.pool_address().to_owned(),
        ));
    }
    if selection.selected_mint != descriptor.base_mint {
        return Err(MarketStateError::UnsupportedOrientation);
    }
    if descriptor.pool_key.protocol_id() != PUMP_PROTOCOL_ID
        && descriptor.pool_key.protocol_id() != PUMP_SWAP_PROTOCOL_ID
        && descriptor.pool_key.protocol_id() != RAYDIUM_AMM_V4_PROTOCOL_ID
        && descriptor.pool_key.protocol_id() != RAYDIUM_CPMM_PROTOCOL_ID
        && descriptor.pool_key.protocol_id() != RAYDIUM_CLMM_PROTOCOL_ID
        && descriptor.pool_key.protocol_id() != METEORA_DAMM_V1_PROTOCOL_ID
        && descriptor.pool_key.protocol_id() != METEORA_DAMM_V2_PROTOCOL_ID
        && descriptor.pool_key.protocol_id() != METEORA_DLMM_PROTOCOL_ID
        && descriptor.pool_key.protocol_id() != ORCA_WHIRLPOOL_PROTOCOL_ID
        && descriptor.pool_key.protocol_id() != MANIFEST_PROTOCOL_ID
    {
        return Err(MarketStateError::UnknownProtocol(
            descriptor.pool_key.protocol_id().to_owned(),
        ));
    }
    if descriptor.pool_key.protocol_id() == METEORA_DAMM_V1_PROTOCOL_ID {
        let roles = [
            "baseVaultState",
            "quoteVaultState",
            "baseVaultLp",
            "quoteVaultLp",
            "baseVaultLpMint",
            "quoteVaultLpMint",
        ];
        if descriptor.base_vault.is_none()
            || descriptor.quote_vault.is_none()
            || roles.iter().any(|role| {
                descriptor
                    .protocol_accounts
                    .iter()
                    .filter(|account| account.role == *role && !account.address.is_empty())
                    .count()
                    != 1
            })
        {
            return Err(MarketStateError::DescriptorMismatch);
        }
    }
    Ok(())
}

fn protocol_account_address<'a>(
    descriptor: &'a crate::domain::PoolDescriptor,
    role: &str,
) -> Option<&'a str> {
    descriptor
        .protocol_accounts
        .iter()
        .find(|account| account.role == role)
        .map(|account| account.address.as_str())
}

fn protocol_account_role<'a>(
    descriptor: &'a crate::domain::PoolDescriptor,
    address: &str,
) -> Option<&'a str> {
    descriptor
        .protocol_accounts
        .iter()
        .find(|account| account.address == address)
        .map(|account| account.role.as_str())
}

fn matches_damm_v1_pool(
    descriptor: &crate::domain::PoolDescriptor,
    state: &meteora_damm_v1::MeteoraDammV1PoolState,
) -> bool {
    let selected_is_a =
        descriptor.base_mint == state.token_a_mint && descriptor.quote_mint == state.token_b_mint;
    let selected_is_b =
        descriptor.base_mint == state.token_b_mint && descriptor.quote_mint == state.token_a_mint;
    if !selected_is_a && !selected_is_b {
        return false;
    }
    let (base_vault_state, quote_vault_state, base_vault_lp, quote_vault_lp) = if selected_is_a {
        (
            &state.a_vault,
            &state.b_vault,
            &state.a_vault_lp,
            &state.b_vault_lp,
        )
    } else {
        (
            &state.b_vault,
            &state.a_vault,
            &state.b_vault_lp,
            &state.a_vault_lp,
        )
    };
    protocol_account_address(descriptor, "baseVaultState") == Some(base_vault_state)
        && protocol_account_address(descriptor, "quoteVaultState") == Some(quote_vault_state)
        && protocol_account_address(descriptor, "baseVaultLp") == Some(base_vault_lp)
        && protocol_account_address(descriptor, "quoteVaultLp") == Some(quote_vault_lp)
}

fn matches_dynamic_vault(
    descriptor: &crate::domain::PoolDescriptor,
    state: &meteora_dynamic_vault::MeteoraDynamicVaultState,
    is_base: bool,
) -> bool {
    let (mint, token_vault, lp_mint_role) = if is_base {
        (
            descriptor.base_mint.as_str(),
            descriptor.base_vault.as_deref(),
            "baseVaultLpMint",
        )
    } else {
        (
            descriptor.quote_mint.as_str(),
            descriptor.quote_vault.as_deref(),
            "quoteVaultLpMint",
        )
    };
    state.enabled
        && state.token_mint == mint
        && Some(state.token_vault.as_str()) == token_vault
        && Some(state.lp_mint.as_str()) == protocol_account_address(descriptor, lp_mint_role)
}

fn matches_oriented_pair(
    descriptor: &crate::domain::PoolDescriptor,
    token_0_mint: &str,
    token_1_mint: &str,
    token_0_vault: &str,
    token_1_vault: &str,
) -> bool {
    let forward = descriptor.base_mint == token_0_mint
        && descriptor.quote_mint == token_1_mint
        && descriptor.base_vault.as_deref() == Some(token_0_vault)
        && descriptor.quote_vault.as_deref() == Some(token_1_vault);
    let reverse = descriptor.base_mint == token_1_mint
        && descriptor.quote_mint == token_0_mint
        && descriptor.base_vault.as_deref() == Some(token_1_vault)
        && descriptor.quote_vault.as_deref() == Some(token_0_vault);
    forward || reverse
}

fn commitment_rank(commitment: &Commitment) -> u8 {
    match commitment {
        Commitment::Processed => 0,
        Commitment::Confirmed => 1,
        Commitment::Finalized => 2,
    }
}

fn current_epoch() -> u64 {
    current_unix_ms().max(1) as u64
}

fn current_unix_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis() as i64)
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::domain::{PoolDescriptor, ProtocolAccount};
    use crate::raw::{RawInstruction, RawProgramData};
    use serde::Deserialize;

    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct GoldenTransactionFixture {
        selections: Vec<WatchedPoolSelection>,
        transaction: RawTransactionUpdate,
        expected: Vec<GoldenTradeExpectation>,
    }

    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct GoldenTradeExpectation {
        protocol_id: String,
        pool_address: String,
        last_trade_coefficient: String,
        last_trade_scale: u32,
        event_id: String,
    }

    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct GoldenAccountFixture {
        accounts: Vec<GoldenAccountState>,
    }

    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct GoldenAccountState {
        protocol_id: String,
        address: String,
        owner_program: String,
        data_base64: String,
        expected: GoldenAccountExpectation,
    }

    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct GoldenAccountExpectation {
        base_mint: Option<String>,
        quote_mint: Option<String>,
        base_vault: Option<String>,
        quote_vault: Option<String>,
    }

    fn descriptor(protocol: &str, pool: &str, base: &str, quote: &str) -> PoolDescriptor {
        PoolDescriptor {
            pool_key: PoolKey {
                deployment_key: crate::domain::DeploymentKey {
                    chain_namespace: "solana".to_owned(),
                    chain_id: "mainnet-beta".to_owned(),
                    protocol_id: protocol.to_owned(),
                    contract_address: String::new(),
                },
                pool_id: pool.to_owned(),
            },
            pool_type: protocol.to_owned(),
            program_id: match protocol {
                PUMP_PROTOCOL_ID => pump::PROGRAM_ID,
                PUMP_SWAP_PROTOCOL_ID => pump_swap::PROGRAM_ID,
                RAYDIUM_AMM_V4_PROTOCOL_ID => raydium_amm_v4::PROGRAM_ID,
                RAYDIUM_CPMM_PROTOCOL_ID => raydium_cpmm::PROGRAM_ID,
                RAYDIUM_CLMM_PROTOCOL_ID => raydium_clmm::PROGRAM_ID,
                METEORA_DAMM_V1_PROTOCOL_ID => meteora_damm_v1::PROGRAM_ID,
                METEORA_DAMM_V2_PROTOCOL_ID => meteora_damm_v2::PROGRAM_ID,
                METEORA_DLMM_PROTOCOL_ID => meteora_dlmm::PROGRAM_ID,
                ORCA_WHIRLPOOL_PROTOCOL_ID => orca_whirlpool::PROGRAM_ID,
                MANIFEST_PROTOCOL_ID => manifest::PROGRAM_ID,
                _ => "unknown",
            }
            .to_owned(),
            base_mint: base.to_owned(),
            quote_mint: quote.to_owned(),
            base_decimals: 6,
            quote_decimals: 9,
            base_vault: None,
            quote_vault: None,
            protocol_accounts: Vec::new(),
            support_status: SupportStatus::Supported,
            support_reason: None,
            asset0: None,
            asset1: None,
            validation_block: None,
            validation_block_hash: None,
            fee_tier: None,
            tick_spacing: None,
            bin_step: None,
            hook_address: None,
            pricing_mode: None,
        }
    }

    fn transfer_instruction(
        outer_instruction_index: u16,
        inner_instruction_index: u16,
        source: &str,
        destination: &str,
        amount: u64,
    ) -> RawInstruction {
        let mut data = vec![3];
        data.extend_from_slice(&amount.to_le_bytes());
        RawInstruction {
            program_id: spl_token::PROGRAM_ID.to_owned(),
            data_base64: BASE64.encode(data),
            outer_instruction_index,
            inner_instruction_index: Some(inner_instruction_index),
            stack_height: Some(3),
            account_addresses: vec![
                source.to_owned(),
                destination.to_owned(),
                "authority".to_owned(),
            ],
        }
    }

    fn token_2022_transfer_instruction(
        outer_instruction_index: u16,
        inner_instruction_index: u16,
        source: &str,
        destination: &str,
        amount: u64,
    ) -> RawInstruction {
        let mut instruction = transfer_instruction(
            outer_instruction_index,
            inner_instruction_index,
            source,
            destination,
            amount,
        );
        instruction.program_id = TOKEN_2022_PROGRAM_ID.to_owned();
        instruction
    }

    #[test]
    fn replacement_is_atomic_when_one_selection_is_invalid() {
        let base = bs58::encode([3_u8; 32]).into_string();
        let mut state = MarketState::new(7);
        state
            .replace_watched_pools(vec![WatchedPoolSelection {
                descriptor: descriptor(PUMP_PROTOCOL_ID, "curve", &base, WRAPPED_SOL_MINT),
                selected_mint: base.clone(),
            }])
            .unwrap();

        let error = state
            .replace_watched_pools(vec![WatchedPoolSelection {
                descriptor: descriptor(PUMP_PROTOCOL_ID, "other", &base, WRAPPED_SOL_MINT),
                selected_mint: WRAPPED_SOL_MINT.to_owned(),
            }])
            .unwrap_err();
        assert!(matches!(error, MarketStateError::UnsupportedOrientation));
        assert_eq!(state.watched_pool_count(), 1);
        assert_eq!(state.snapshots()[0].pool_key.pool_address(), "curve");
    }

    #[test]
    fn pump_curve_account_produces_exact_spot_price_and_ignores_duplicate_write() {
        let base = bs58::encode([3_u8; 32]).into_string();
        let quote = bs58::encode([9_u8; 32]).into_string();
        let mut state = MarketState::new(7);
        state
            .replace_watched_pools(vec![WatchedPoolSelection {
                descriptor: descriptor(PUMP_PROTOCOL_ID, "curve", &base, &quote),
                selected_mint: base,
            }])
            .unwrap();
        state.set_recovery_state(RecoveryState::Live);

        let mut data = vec![23, 183, 248, 55, 96, 216, 172, 96];
        for value in [1_000_000_u64, 2_000_000_000, 0, 0, 0] {
            data.extend_from_slice(&value.to_le_bytes());
        }
        data.push(0);
        data.extend_from_slice(&[7_u8; 32]);
        data.push(0);
        data.push(0);
        data.extend_from_slice(&[9_u8; 32]);
        let update = RawAccountUpdate {
            pubkey: "curve".to_owned(),
            owner_program: pump::PROGRAM_ID.to_owned(),
            data_base64: BASE64.encode(data),
            slot: 10,
            write_version: 1,
            commitment: Commitment::Processed,
            source_id: "fixture".to_owned(),
            observed_at_unix_ms: 100,
        };

        let first = state.apply_account_update(update.clone()).unwrap();
        assert_eq!(first.len(), 1);
        assert_eq!(
            first[0].spot_price_quote.as_ref().unwrap().coefficient,
            "2000000000000000000"
        );
        assert!(state.apply_account_update(update).unwrap().is_empty());
    }

    #[test]
    fn canonical_solana_stable_quotes_have_usd_prices_without_an_external_reference() {
        let base = bs58::encode([3_u8; 32]).into_string();
        let mut state = MarketState::new(7);
        state
            .replace_watched_pools(vec![WatchedPoolSelection {
                descriptor: descriptor(PUMP_PROTOCOL_ID, "curve", &base, SOLANA_USDC_MINT),
                selected_mint: base,
            }])
            .unwrap();
        let mut initial = state.snapshots().remove(0);
        initial.spot_price_quote = Some(DecimalValue {
            coefficient: "12345".to_owned(),
            scale: 2,
        });
        let observed_at = 1_700_000_000_000;
        initial.observed_at_unix_ms = observed_at;

        for (mint, symbol) in [(SOLANA_USDC_MINT, "USDC"), (SOLANA_USDT_MINT, "USDT")] {
            let mut update = initial.clone();
            update.quote_mint = mint.to_owned();
            apply_reference_prices(&mut update, None, observed_at);
            assert_eq!(update.price_usd, update.spot_price_quote);
            assert_eq!(
                update.reference_source_id.as_deref(),
                Some(format!("solana:onchain:{symbol}-quote").as_str())
            );
            assert_eq!(
                update.reference_observed_at_unix_ms,
                Some(update.observed_at_unix_ms)
            );
            assert!(update.price_sol.is_none());
        }

        let mut unknown = initial;
        unknown.quote_mint = bs58::encode([9_u8; 32]).into_string();
        apply_reference_prices(&mut unknown, None, observed_at);
        assert!(unknown.price_usd.is_none());
        assert!(unknown.reference_source_id.is_none());
    }

    #[test]
    fn sol_reference_enriches_wsol_prices_without_replacing_chain_timestamps() {
        let base = bs58::encode([3_u8; 32]).into_string();
        let mut state = MarketState::new(7);
        state
            .replace_watched_pools(vec![WatchedPoolSelection {
                descriptor: descriptor(PUMP_PROTOCOL_ID, "curve", &base, WRAPPED_SOL_MINT),
                selected_mint: base,
            }])
            .unwrap();

        let chain_observed_at = current_unix_ms() - 1;
        let mut data = vec![23, 183, 248, 55, 96, 216, 172, 96];
        for value in [1_000_000_u64, 2_000_000_000, 0, 0, 0] {
            data.extend_from_slice(&value.to_le_bytes());
        }
        data.push(0);
        data.extend_from_slice(&[7_u8; 32]);
        data.push(0);
        data.push(0);
        data.extend_from_slice(&bs58::decode(WRAPPED_SOL_MINT).into_vec().unwrap());
        let initial = state
            .apply_account_update(RawAccountUpdate {
                pubkey: "curve".to_owned(),
                owner_program: pump::PROGRAM_ID.to_owned(),
                data_base64: BASE64.encode(data),
                slot: 10,
                write_version: 1,
                commitment: Commitment::Processed,
                source_id: "fixture".to_owned(),
                observed_at_unix_ms: chain_observed_at,
            })
            .unwrap();
        assert_eq!(initial[0].price_sol, initial[0].spot_price_quote);
        assert!(initial[0].price_usd.is_none());

        let reference_observed_at = current_unix_ms();
        let enriched = state
            .apply_sol_usd_reference(
                DecimalValue {
                    coefficient: "15025".to_owned(),
                    scale: 2,
                },
                "ccxt:binance:SOL/USDT".to_owned(),
                reference_observed_at,
            )
            .unwrap();
        assert_eq!(enriched.len(), 1);
        assert_eq!(
            enriched[0].price_usd.as_ref().unwrap().coefficient,
            "30050000000000000000000"
        );
        assert_eq!(enriched[0].observed_at_unix_ms, chain_observed_at);
        assert_eq!(
            enriched[0].reference_source_id.as_deref(),
            Some("ccxt:binance:SOL/USDT")
        );
        assert_eq!(
            enriched[0].reference_observed_at_unix_ms,
            Some(reference_observed_at)
        );

        let stale = state
            .apply_sol_usd_reference(
                DecimalValue {
                    coefficient: "15025".to_owned(),
                    scale: 2,
                },
                "stale".to_owned(),
                current_unix_ms() - REFERENCE_PRICE_STALE_AFTER_MS - 1,
            )
            .unwrap();
        assert!(stale[0].price_usd.is_none());
        assert!(stale[0].price_sol.is_some());
        assert!(stale[0].reference_source_id.is_none());
    }

    #[test]
    fn pump_swap_waits_for_validated_pool_and_both_vaults_before_emitting_spot_price() {
        let base_bytes = [3_u8; 32];
        let quote_bytes = [9_u8; 32];
        let base = bs58::encode(base_bytes).into_string();
        let quote = bs58::encode(quote_bytes).into_string();
        let base_vault = bs58::encode([4_u8; 32]).into_string();
        let quote_vault = bs58::encode([5_u8; 32]).into_string();
        let pool = bs58::encode([8_u8; 32]).into_string();
        let mut pool_descriptor = descriptor(PUMP_SWAP_PROTOCOL_ID, &pool, &base, &quote);
        pool_descriptor.base_vault = Some(base_vault.clone());
        pool_descriptor.quote_vault = Some(quote_vault.clone());

        let mut state = MarketState::new(7);
        state
            .replace_watched_pools(vec![WatchedPoolSelection {
                descriptor: pool_descriptor,
                selected_mint: base.clone(),
            }])
            .unwrap();
        state.set_recovery_state(RecoveryState::Live);

        let mut pool_data = vec![241, 154, 109, 4, 17, 177, 109, 188];
        pool_data.push(254);
        pool_data.extend_from_slice(&1_u16.to_le_bytes());
        pool_data.extend_from_slice(&[1_u8; 32]);
        pool_data.extend_from_slice(&base_bytes);
        pool_data.extend_from_slice(&quote_bytes);
        pool_data.extend_from_slice(&[6_u8; 32]);
        pool_data.extend_from_slice(&[4_u8; 32]);
        pool_data.extend_from_slice(&[5_u8; 32]);
        pool_data.extend_from_slice(&1_000_000_u64.to_le_bytes());
        pool_data.extend_from_slice(&[7_u8; 32]);
        pool_data.push(0);
        pool_data.push(0);
        pool_data.extend_from_slice(&1_000_000_000_i128.to_le_bytes());

        let account_update =
            |pubkey: String, owner_program: &str, data: Vec<u8>, write_version| RawAccountUpdate {
                pubkey,
                owner_program: owner_program.to_owned(),
                data_base64: BASE64.encode(data),
                slot: 10,
                write_version,
                commitment: Commitment::Processed,
                source_id: "fixture".to_owned(),
                observed_at_unix_ms: 100,
            };
        assert!(state
            .apply_account_update(account_update(
                pool.clone(),
                pump_swap::PROGRAM_ID,
                pool_data.clone(),
                1
            ))
            .unwrap()
            .is_empty());

        let token_account = |mint: [u8; 32], amount: u64| {
            let mut data = vec![0_u8; 165];
            data[..32].copy_from_slice(&mint);
            data[64..72].copy_from_slice(&amount.to_le_bytes());
            data
        };
        assert!(state
            .apply_account_update(account_update(
                base_vault.clone(),
                TOKEN_PROGRAM_ID,
                token_account(base_bytes, 1_000_000),
                2
            ))
            .unwrap()
            .is_empty());
        let updates = state
            .apply_account_update(account_update(
                quote_vault.clone(),
                TOKEN_PROGRAM_ID,
                token_account(quote_bytes, 2_000_000_000),
                3,
            ))
            .unwrap();

        assert_eq!(updates.len(), 1);
        assert_eq!(
            updates[0].spot_price_quote.as_ref().unwrap().coefficient,
            "3000000000000000000"
        );
        let sample = vec![
            account_update(pool, pump_swap::PROGRAM_ID, pool_data, 4),
            account_update(
                base_vault,
                TOKEN_PROGRAM_ID,
                token_account(base_bytes, 2_000_000),
                4,
            ),
            account_update(
                quote_vault,
                TOKEN_PROGRAM_ID,
                token_account(quote_bytes, 4_000_000_000),
                4,
            ),
        ];
        let mut invalid = sample.clone();
        invalid[2].owner_program = "wrong-owner".to_owned();
        let before = state.snapshots();
        assert!(state.apply_account_snapshot(invalid).is_err());
        assert_eq!(state.snapshots(), before);
        assert!(state
            .apply_account_snapshot(sample[1..].to_vec())
            .unwrap()
            .is_empty());
        assert_eq!(state.snapshots(), before);
        let prices = state.apply_account_snapshot(sample.clone()).unwrap();
        assert_eq!(prices.len(), 1);
        assert_eq!(
            prices[0].spot_price_quote.as_ref().unwrap().coefficient,
            "2500000000000000000"
        );
        let mut mixed_slots = sample;
        mixed_slots[2].slot += 1;
        assert!(state.apply_account_snapshot(mixed_slots).is_err());
    }

    #[test]
    fn non_pump_swap_prices_its_mixed_token_program_child_vault_transfers() {
        let base = bs58::encode([3_u8; 32]).into_string();
        let quote = bs58::encode([9_u8; 32]).into_string();
        let pool = bs58::encode([8_u8; 32]).into_string();
        let base_vault = bs58::encode([4_u8; 32]).into_string();
        let quote_vault = bs58::encode([5_u8; 32]).into_string();
        let mut pool_descriptor = descriptor(RAYDIUM_CPMM_PROTOCOL_ID, &pool, &base, &quote);
        pool_descriptor.base_vault = Some(base_vault.clone());
        pool_descriptor.quote_vault = Some(quote_vault.clone());

        let mut state = MarketState::new(7);
        state
            .replace_watched_pools(vec![WatchedPoolSelection {
                descriptor: pool_descriptor,
                selected_mint: base.clone(),
            }])
            .unwrap();
        state.set_recovery_state(RecoveryState::Live);

        let transaction = RawTransactionUpdate {
            signature: "raydium-signature".to_owned(),
            slot: 44,
            failed: false,
            commitment: Commitment::Processed,
            source_id: "fixture".to_owned(),
            observed_at_unix_ms: 400,
            instructions: vec![
                RawInstruction {
                    program_id: raydium_cpmm::PROGRAM_ID.to_owned(),
                    data_base64: BASE64.encode([143, 190, 90, 218, 196, 30, 51, 222]),
                    outer_instruction_index: 2,
                    inner_instruction_index: Some(1),
                    stack_height: Some(2),
                    account_addresses: vec![pool.clone(), base_vault.clone(), quote_vault.clone()],
                },
                token_2022_transfer_instruction(2, 2, &base_vault, "user-base", 1_000_000),
                transfer_instruction(2, 3, "user-quote", &quote_vault, 2_000_000_000),
            ],
            program_data: vec![],
            token_balances: vec![],
        };

        let updates = state.apply_transaction_update(transaction.clone()).unwrap();
        assert_eq!(updates.len(), 1);
        assert_eq!(
            updates[0]
                .last_trade_price_quote
                .as_ref()
                .unwrap()
                .coefficient,
            "2000000000000000000"
        );
        assert!(
            updates[0].last_trade_event_id.as_deref().is_some_and(
                |event_id| event_id.starts_with("raydium-signature:2:1:raydiumCpmmSwap:")
            )
        );
        assert!(state
            .apply_transaction_update(transaction)
            .unwrap()
            .is_empty());
    }

    #[test]
    fn two_swaps_through_one_pool_remain_two_distinct_trades() {
        let base = bs58::encode([3_u8; 32]).into_string();
        let quote = bs58::encode([9_u8; 32]).into_string();
        let pool = bs58::encode([8_u8; 32]).into_string();
        let base_vault = bs58::encode([4_u8; 32]).into_string();
        let quote_vault = bs58::encode([5_u8; 32]).into_string();
        let mut pool_descriptor = descriptor(RAYDIUM_CPMM_PROTOCOL_ID, &pool, &base, &quote);
        pool_descriptor.base_vault = Some(base_vault.clone());
        pool_descriptor.quote_vault = Some(quote_vault.clone());
        let mut state = MarketState::new(7);
        state
            .replace_watched_pools(vec![WatchedPoolSelection {
                descriptor: pool_descriptor,
                selected_mint: base,
            }])
            .unwrap();

        let swap_instruction = |inner_instruction_index| RawInstruction {
            program_id: raydium_cpmm::PROGRAM_ID.to_owned(),
            data_base64: BASE64.encode([143, 190, 90, 218, 196, 30, 51, 222]),
            outer_instruction_index: 4,
            inner_instruction_index: Some(inner_instruction_index),
            stack_height: Some(2),
            account_addresses: vec![pool.clone(), base_vault.clone(), quote_vault.clone()],
        };
        let transaction = RawTransactionUpdate {
            signature: "two-swaps".to_owned(),
            slot: 45,
            failed: false,
            commitment: Commitment::Processed,
            source_id: "fixture".to_owned(),
            observed_at_unix_ms: 450,
            instructions: vec![
                swap_instruction(1),
                transfer_instruction(4, 2, &base_vault, "first-user-base", 1_000_000),
                transfer_instruction(4, 3, "first-user-quote", &quote_vault, 2_000_000_000),
                swap_instruction(4),
                transfer_instruction(4, 5, "second-user-base", &base_vault, 2_000_000),
                transfer_instruction(4, 6, &quote_vault, "second-user-quote", 3_000_000_000),
            ],
            program_data: vec![],
            token_balances: vec![],
        };

        let updates = state.apply_transaction_update(transaction).unwrap();
        assert_eq!(updates.len(), 2);
        assert_eq!(
            updates[0]
                .last_trade_price_quote
                .as_ref()
                .unwrap()
                .coefficient,
            "2000000000000000000"
        );
        assert_eq!(
            updates[1]
                .last_trade_price_quote
                .as_ref()
                .unwrap()
                .coefficient,
            "1500000000000000000"
        );
        assert_ne!(
            updates[0].last_trade_event_id,
            updates[1].last_trade_event_id
        );
    }

    #[test]
    fn recorded_mainnet_route_decodes_orca_and_cpmm_swaps_independently() {
        assert_golden_fixture(include_str!(
            "../tests/fixtures/mainnet-orca-cpmm-route-443381510.json"
        ));
    }

    #[test]
    fn recorded_mainnet_clmm_and_dlmm_swaps_decode_exact_vault_transfers() {
        for fixture in [
            include_str!("../tests/fixtures/mainnet-raydium-clmm-443398559.json"),
            include_str!("../tests/fixtures/mainnet-meteora-dlmm-443398971.json"),
        ] {
            assert_golden_fixture(fixture);
        }
    }

    #[test]
    fn recorded_mainnet_pump_log_emits_only_for_the_successful_transaction() {
        for fixture in [
            include_str!("../tests/fixtures/mainnet-pump-buy-346729519.json"),
            include_str!("../tests/fixtures/mainnet-pump-sell-443448226.json"),
            include_str!("../tests/fixtures/mainnet-pump-failed-345913699.json"),
            include_str!("../tests/fixtures/mainnet-pump-swap-buy-443447386.json"),
            include_str!("../tests/fixtures/mainnet-pump-swap-sell-404654887.json"),
        ] {
            assert_golden_fixture(fixture);
        }
    }

    #[test]
    fn recorded_mainnet_pool_accounts_decode_for_every_supported_family() {
        let fixture: GoldenAccountFixture = serde_json::from_str(include_str!(
            "../tests/fixtures/mainnet-account-states-443415340.json"
        ))
        .unwrap();
        assert_eq!(fixture.accounts.len(), 6);

        for account in fixture.accounts {
            let data = BASE64.decode(account.data_base64).unwrap();
            match account.protocol_id.as_str() {
                PUMP_PROTOCOL_ID => {
                    let state = pump::decode_bonding_curve(&account.owner_program, &data).unwrap();
                    assert_eq!(Some(state.quote_mint), account.expected.quote_mint);
                }
                PUMP_SWAP_PROTOCOL_ID => {
                    let state = pump_swap::decode_pool(&account.owner_program, &data).unwrap();
                    assert_eq!(Some(state.base_mint), account.expected.base_mint);
                    assert_eq!(Some(state.quote_mint), account.expected.quote_mint);
                    assert_eq!(
                        Some(state.pool_base_token_account),
                        account.expected.base_vault
                    );
                    assert_eq!(
                        Some(state.pool_quote_token_account),
                        account.expected.quote_vault
                    );
                }
                RAYDIUM_CPMM_PROTOCOL_ID => {
                    let state = raydium_cpmm::decode_pool(&account.owner_program, &data).unwrap();
                    assert_eq!(Some(state.token_0_mint), account.expected.base_mint);
                    assert_eq!(Some(state.token_1_mint), account.expected.quote_mint);
                    assert_eq!(Some(state.token_0_vault), account.expected.base_vault);
                    assert_eq!(Some(state.token_1_vault), account.expected.quote_vault);
                }
                RAYDIUM_CLMM_PROTOCOL_ID => {
                    let state = raydium_clmm::decode_pool(&account.owner_program, &data).unwrap();
                    assert_eq!(Some(state.token_0_mint), account.expected.base_mint);
                    assert_eq!(Some(state.token_1_mint), account.expected.quote_mint);
                    assert_eq!(Some(state.token_0_vault), account.expected.base_vault);
                    assert_eq!(Some(state.token_1_vault), account.expected.quote_vault);
                    assert!(state.sqrt_price_x64 > 0);
                }
                METEORA_DLMM_PROTOCOL_ID => {
                    let state = meteora_dlmm::decode_pool(&account.owner_program, &data).unwrap();
                    assert_eq!(Some(state.token_x_mint), account.expected.base_mint);
                    assert_eq!(Some(state.token_y_mint), account.expected.quote_mint);
                    assert_eq!(Some(state.reserve_x), account.expected.base_vault);
                    assert_eq!(Some(state.reserve_y), account.expected.quote_vault);
                }
                "orcaWhirlpoolImmutable" => {
                    let state = orca_whirlpool::decode_pool(&account.owner_program, &data).unwrap();
                    assert_eq!(account.owner_program, orca_whirlpool::IMMUTABLE_PROGRAM_ID);
                    assert!(state.sqrt_price_x64 > 0);
                }
                protocol => panic!("unexpected account fixture protocol {protocol}"),
            }
            assert!(!account.address.is_empty());
        }
    }

    #[test]
    fn attributed_program_data_routes_pump_events_and_deduplicates_replay() {
        let mint_bytes = [3_u8; 32];
        let mint = bs58::encode(mint_bytes).into_string();
        let pump_swap_pool = bs58::encode([4_u8; 32]).into_string();
        let mut state = MarketState::new(7);
        state
            .replace_watched_pools(vec![
                WatchedPoolSelection {
                    descriptor: descriptor(PUMP_PROTOCOL_ID, "curve", &mint, WRAPPED_SOL_MINT),
                    selected_mint: mint.clone(),
                },
                WatchedPoolSelection {
                    descriptor: descriptor(
                        PUMP_SWAP_PROTOCOL_ID,
                        &pump_swap_pool,
                        &mint,
                        WRAPPED_SOL_MINT,
                    ),
                    selected_mint: mint.clone(),
                },
            ])
            .unwrap();

        let mut pump_event = vec![189, 219, 127, 211, 78, 230, 97, 238];
        pump_event.extend_from_slice(&mint_bytes);
        pump_event.extend_from_slice(&2_000_000_000_u64.to_le_bytes());
        pump_event.extend_from_slice(&1_000_000_u64.to_le_bytes());
        pump_event.push(1);

        let mut pump_swap_event = vec![0_u8; 152];
        pump_swap_event[..8].copy_from_slice(&[62, 47, 55, 10, 165, 3, 220, 42]);
        pump_swap_event[16..24].copy_from_slice(&1_000_000_u64.to_le_bytes());
        pump_swap_event[112..120].copy_from_slice(&2_000_000_000_u64.to_le_bytes());
        pump_swap_event[120..152].copy_from_slice(&[4_u8; 32]);

        let transaction = RawTransactionUpdate {
            signature: "program-data-signature".to_owned(),
            slot: 47,
            failed: false,
            commitment: Commitment::Processed,
            source_id: "fixture".to_owned(),
            observed_at_unix_ms: 470,
            instructions: vec![],
            program_data: vec![
                RawProgramData {
                    program_id: "unrelated-program".to_owned(),
                    data_base64: BASE64.encode(&pump_event),
                    log_index: 2,
                },
                RawProgramData {
                    program_id: pump::PROGRAM_ID.to_owned(),
                    data_base64: BASE64.encode(&pump_event),
                    log_index: 9,
                },
                RawProgramData {
                    program_id: pump_swap::PROGRAM_ID.to_owned(),
                    data_base64: BASE64.encode(&pump_swap_event),
                    log_index: 15,
                },
            ],
            token_balances: vec![],
        };

        let updates = state.apply_transaction_update(transaction.clone()).unwrap();
        assert_eq!(updates.len(), 2);
        assert!(updates.iter().any(|update| {
            update.last_trade_event_id.as_deref()
                == Some("program-data-signature:log:9:pumpTrade:curve")
        }));
        let pump_swap_event_id =
            format!("program-data-signature:log:15:pumpSwapTrade:{pump_swap_pool}");
        assert!(updates.iter().any(|update| {
            update.last_trade_event_id.as_deref() == Some(pump_swap_event_id.as_str())
        }));
        assert!(state
            .apply_transaction_update(transaction.clone())
            .unwrap()
            .is_empty());

        let mut failed = transaction;
        failed.signature = "failed-program-data".to_owned();
        failed.failed = true;
        failed.program_data[1].data_base64 = "not base64".to_owned();
        assert!(state.apply_transaction_update(failed).unwrap().is_empty());
    }

    #[test]
    fn manifest_fill_routes_by_market_and_inverts_reverse_orientation() {
        let base_bytes = [3_u8; 32];
        let quote_bytes = [9_u8; 32];
        let market_bytes = [8_u8; 32];
        let base = bs58::encode(base_bytes).into_string();
        let quote = bs58::encode(quote_bytes).into_string();
        let market = bs58::encode(market_bytes).into_string();
        let selection = WatchedPoolSelection {
            descriptor: descriptor(MANIFEST_PROTOCOL_ID, &market, &base, &quote),
            selected_mint: base,
        };
        let mut state = MarketState::new(7);
        state.replace_watched_pools(vec![selection]).unwrap();

        let mut fill = vec![0_u8; 232];
        fill[..8].copy_from_slice(&manifest::FILL_LOG_DISCRIMINANT);
        fill[8..40].copy_from_slice(&market_bytes);
        fill[104..136].copy_from_slice(&quote_bytes);
        fill[136..168].copy_from_slice(&base_bytes);
        fill[184..192].copy_from_slice(&2_000_000_000_u64.to_le_bytes());
        fill[192..200].copy_from_slice(&1_000_000_u64.to_le_bytes());
        fill[216] = 1;
        let transaction = RawTransactionUpdate {
            signature: "manifest-fill".to_owned(),
            slot: 52,
            failed: false,
            commitment: Commitment::Confirmed,
            source_id: "fixture".to_owned(),
            observed_at_unix_ms: 520,
            instructions: vec![],
            program_data: vec![RawProgramData {
                program_id: manifest::PROGRAM_ID.to_owned(),
                data_base64: BASE64.encode(fill),
                log_index: 3,
            }],
            token_balances: vec![],
        };
        let updates = state.apply_transaction_update(transaction).unwrap();
        assert_eq!(updates.len(), 1);
        assert_eq!(
            updates[0]
                .last_trade_price_quote
                .as_ref()
                .unwrap()
                .coefficient,
            "2000000000000000000"
        );
        assert!(updates[0]
            .last_trade_event_id
            .as_deref()
            .unwrap()
            .contains("manifestFill"));
    }

    fn assert_golden_fixture(json: &str) {
        let fixture: GoldenTransactionFixture = serde_json::from_str(json).unwrap();
        let expected_slot = fixture.transaction.slot;
        let mut state = MarketState::new(7);
        state.replace_watched_pools(fixture.selections).unwrap();

        let updates = state
            .apply_transaction_update(fixture.transaction.clone())
            .unwrap();
        assert_eq!(updates.len(), fixture.expected.len());
        for expected in fixture.expected {
            let update = updates
                .iter()
                .find(|update| {
                    update.pool_key.protocol_id() == expected.protocol_id
                        && update.pool_key.pool_address() == expected.pool_address
                })
                .expect("golden pool update");
            let price = update
                .last_trade_price_quote
                .as_ref()
                .expect("golden last-trade price");
            assert_eq!(price.coefficient, expected.last_trade_coefficient);
            assert_eq!(price.scale, expected.last_trade_scale);
            assert_eq!(
                update.last_trade_event_id.as_deref(),
                Some(expected.event_id.as_str())
            );
            assert_eq!(update.slot, expected_slot);
            assert_eq!(update.commitment, Commitment::Confirmed);
        }
        assert!(state
            .apply_transaction_update(fixture.transaction)
            .unwrap()
            .is_empty());
    }

    #[test]
    fn every_supported_non_pump_family_requires_its_real_swap_discriminator() {
        let base = bs58::encode([3_u8; 32]).into_string();
        let quote = bs58::encode([9_u8; 32]).into_string();
        let pool = bs58::encode([8_u8; 32]).into_string();
        let base_vault = bs58::encode([4_u8; 32]).into_string();
        let quote_vault = bs58::encode([5_u8; 32]).into_string();
        for (protocol, discriminator) in [
            (
                RAYDIUM_CPMM_PROTOCOL_ID,
                [143, 190, 90, 218, 196, 30, 51, 222],
            ),
            (RAYDIUM_CLMM_PROTOCOL_ID, [43, 4, 237, 11, 26, 201, 30, 98]),
            (
                METEORA_DAMM_V1_PROTOCOL_ID,
                [248, 198, 158, 145, 225, 117, 135, 200],
            ),
            (
                METEORA_DAMM_V2_PROTOCOL_ID,
                [65, 75, 63, 76, 235, 91, 91, 136],
            ),
            (METEORA_DLMM_PROTOCOL_ID, [65, 75, 63, 76, 235, 91, 91, 136]),
            (
                ORCA_WHIRLPOOL_PROTOCOL_ID,
                [248, 198, 158, 145, 225, 117, 135, 200],
            ),
        ] {
            let mut pool_descriptor = descriptor(protocol, &pool, &base, &quote);
            pool_descriptor.base_vault = Some(base_vault.clone());
            pool_descriptor.quote_vault = Some(quote_vault.clone());
            if protocol == METEORA_DAMM_V1_PROTOCOL_ID {
                pool_descriptor.protocol_accounts = [
                    "baseVaultState",
                    "quoteVaultState",
                    "baseVaultLp",
                    "quoteVaultLp",
                    "baseVaultLpMint",
                    "quoteVaultLpMint",
                ]
                .into_iter()
                .enumerate()
                .map(|(index, role)| ProtocolAccount {
                    role: role.to_owned(),
                    address: bs58::encode([20 + index as u8; 32]).into_string(),
                    index: None,
                })
                .collect();
            }
            let mut state = MarketState::new(7);
            state
                .replace_watched_pools(vec![WatchedPoolSelection {
                    descriptor: pool_descriptor.clone(),
                    selected_mint: base.clone(),
                }])
                .unwrap();
            let transaction = |data: [u8; 8]| RawTransactionUpdate {
                signature: format!("{protocol}-{data:?}"),
                slot: 46,
                failed: false,
                commitment: Commitment::Processed,
                source_id: "fixture".to_owned(),
                observed_at_unix_ms: 460,
                instructions: vec![
                    RawInstruction {
                        program_id: pool_descriptor.program_id.clone(),
                        data_base64: BASE64.encode(data),
                        outer_instruction_index: 0,
                        inner_instruction_index: None,
                        stack_height: None,
                        account_addresses: vec![pool.clone()],
                    },
                    transfer_instruction(0, 0, &base_vault, "user-base", 1_000_000),
                    transfer_instruction(0, 1, "user-quote", &quote_vault, 2_000_000_000),
                ],
                program_data: vec![],
                token_balances: vec![],
            };

            assert_eq!(
                state
                    .apply_transaction_update(transaction(discriminator))
                    .unwrap()
                    .len(),
                1
            );
            assert!(state
                .apply_transaction_update(transaction([0; 8]))
                .unwrap()
                .is_empty());
        }
    }

    #[test]
    fn concentrated_and_discrete_pool_accounts_emit_protocol_spot_prices() {
        let base_bytes = [3_u8; 32];
        let quote_bytes = [9_u8; 32];
        let base_vault_bytes = [4_u8; 32];
        let quote_vault_bytes = [5_u8; 32];
        let base = bs58::encode(base_bytes).into_string();
        let quote = bs58::encode(quote_bytes).into_string();
        let base_vault = bs58::encode(base_vault_bytes).into_string();
        let quote_vault = bs58::encode(quote_vault_bytes).into_string();

        let mut clmm = vec![0_u8; raydium_clmm::POOL_ACCOUNT_LEN];
        clmm[..8].copy_from_slice(&[247, 237, 227, 245, 215, 195, 222, 70]);
        clmm[73..105].copy_from_slice(&base_bytes);
        clmm[105..137].copy_from_slice(&quote_bytes);
        clmm[137..169].copy_from_slice(&base_vault_bytes);
        clmm[169..201].copy_from_slice(&quote_vault_bytes);
        clmm[253..269].copy_from_slice(&(1_u128 << 64).to_le_bytes());

        let mut damm_v2 = vec![0_u8; meteora_damm_v2::POOL_ACCOUNT_LEN];
        damm_v2[..8].copy_from_slice(&[241, 154, 109, 4, 17, 177, 109, 188]);
        damm_v2[168..200].copy_from_slice(&base_bytes);
        damm_v2[200..232].copy_from_slice(&quote_bytes);
        damm_v2[232..264].copy_from_slice(&base_vault_bytes);
        damm_v2[264..296].copy_from_slice(&quote_vault_bytes);
        damm_v2[456..472].copy_from_slice(&(1_u128 << 64).to_le_bytes());

        let mut dlmm = vec![0_u8; meteora_dlmm::LB_PAIR_ACCOUNT_LEN];
        dlmm[..8].copy_from_slice(&[33, 11, 49, 98, 181, 101, 177, 13]);
        dlmm[80..82].copy_from_slice(&25_u16.to_le_bytes());
        dlmm[88..120].copy_from_slice(&base_bytes);
        dlmm[120..152].copy_from_slice(&quote_bytes);
        dlmm[152..184].copy_from_slice(&base_vault_bytes);
        dlmm[184..216].copy_from_slice(&quote_vault_bytes);

        let mut whirlpool = vec![0_u8; orca_whirlpool::WHIRLPOOL_ACCOUNT_LEN];
        whirlpool[..8].copy_from_slice(&[63, 149, 209, 12, 225, 128, 99, 9]);
        whirlpool[65..81].copy_from_slice(&(1_u128 << 64).to_le_bytes());
        whirlpool[101..133].copy_from_slice(&base_bytes);
        whirlpool[133..165].copy_from_slice(&base_vault_bytes);
        whirlpool[181..213].copy_from_slice(&quote_bytes);
        whirlpool[213..245].copy_from_slice(&quote_vault_bytes);

        for (protocol, owner, data) in [
            (RAYDIUM_CLMM_PROTOCOL_ID, raydium_clmm::PROGRAM_ID, clmm),
            (
                METEORA_DAMM_V2_PROTOCOL_ID,
                meteora_damm_v2::PROGRAM_ID,
                damm_v2,
            ),
            (METEORA_DLMM_PROTOCOL_ID, meteora_dlmm::PROGRAM_ID, dlmm),
            (
                ORCA_WHIRLPOOL_PROTOCOL_ID,
                orca_whirlpool::IMMUTABLE_PROGRAM_ID,
                whirlpool,
            ),
        ] {
            let pool = format!("{protocol}-pool");
            let mut pool_descriptor = descriptor(protocol, &pool, &base, &quote);
            pool_descriptor.base_vault = Some(base_vault.clone());
            pool_descriptor.quote_vault = Some(quote_vault.clone());
            pool_descriptor.program_id = owner.to_owned();
            let mut state = MarketState::new(7);
            state
                .replace_watched_pools(vec![WatchedPoolSelection {
                    descriptor: pool_descriptor,
                    selected_mint: base.clone(),
                }])
                .unwrap();
            state.set_recovery_state(RecoveryState::Live);

            let updates = state
                .apply_account_update(RawAccountUpdate {
                    pubkey: pool,
                    owner_program: owner.to_owned(),
                    data_base64: BASE64.encode(data),
                    slot: 50,
                    write_version: 1,
                    commitment: Commitment::Processed,
                    source_id: "fixture".to_owned(),
                    observed_at_unix_ms: 500,
                })
                .unwrap();
            assert_eq!(updates.len(), 1, "{protocol} did not emit a spot update");
            assert_eq!(
                updates[0].spot_price_quote.as_ref().unwrap().coefficient,
                "1000000000000000",
                "{protocol} applied the wrong Q64/bin decimal orientation"
            );
        }
    }

    #[test]
    fn raydium_amm_v4_subtracts_pending_pnl_from_both_vaults() {
        let base_bytes = [3_u8; 32];
        let quote_bytes = [9_u8; 32];
        let base_vault_bytes = [4_u8; 32];
        let quote_vault_bytes = [5_u8; 32];
        let base = bs58::encode(base_bytes).into_string();
        let quote = bs58::encode(quote_bytes).into_string();
        let base_vault = bs58::encode(base_vault_bytes).into_string();
        let quote_vault = bs58::encode(quote_vault_bytes).into_string();
        let pool = bs58::encode([8_u8; 32]).into_string();
        let mut pool_descriptor = descriptor(RAYDIUM_AMM_V4_PROTOCOL_ID, &pool, &base, &quote);
        pool_descriptor.base_vault = Some(base_vault.clone());
        pool_descriptor.quote_vault = Some(quote_vault.clone());
        let mut state = MarketState::new(7);
        state
            .replace_watched_pools(vec![WatchedPoolSelection {
                descriptor: pool_descriptor,
                selected_mint: base.clone(),
            }])
            .unwrap();
        state.set_recovery_state(RecoveryState::Live);

        let mut pool_data = vec![0_u8; raydium_amm_v4::POOL_ACCOUNT_LEN];
        pool_data[0..8].copy_from_slice(&6_u64.to_le_bytes());
        pool_data[192..200].copy_from_slice(&100_000_u64.to_le_bytes());
        pool_data[200..208].copy_from_slice(&200_000_000_u64.to_le_bytes());
        pool_data[336..368].copy_from_slice(&base_vault_bytes);
        pool_data[368..400].copy_from_slice(&quote_vault_bytes);
        pool_data[400..432].copy_from_slice(&base_bytes);
        pool_data[432..464].copy_from_slice(&quote_bytes);

        let account_update =
            |pubkey: String, owner_program: &str, data: Vec<u8>, write_version| RawAccountUpdate {
                pubkey,
                owner_program: owner_program.to_owned(),
                data_base64: BASE64.encode(data),
                slot: 59,
                write_version,
                commitment: Commitment::Processed,
                source_id: "fixture".to_owned(),
                observed_at_unix_ms: 590,
            };
        let token_account = |mint: [u8; 32], amount: u64| {
            let mut data = vec![0_u8; 165];
            data[..32].copy_from_slice(&mint);
            data[64..72].copy_from_slice(&amount.to_le_bytes());
            data
        };

        assert!(state
            .apply_account_update(account_update(
                pool,
                raydium_amm_v4::PROGRAM_ID,
                pool_data,
                1,
            ))
            .unwrap()
            .is_empty());
        assert!(state
            .apply_account_update(account_update(
                base_vault,
                TOKEN_PROGRAM_ID,
                token_account(base_bytes, 1_100_000),
                2,
            ))
            .unwrap()
            .is_empty());
        let updates = state
            .apply_account_update(account_update(
                quote_vault,
                TOKEN_PROGRAM_ID,
                token_account(quote_bytes, 2_200_000_000),
                3,
            ))
            .unwrap();
        assert_eq!(
            updates[0].spot_price_quote.as_ref().unwrap().coefficient,
            "2000000000000000000"
        );
    }

    #[test]
    fn meteora_damm_v1_waits_for_all_dynamic_vault_dependencies() {
        let base_bytes = [3_u8; 32];
        let quote_bytes = [9_u8; 32];
        let base_vault_bytes = [4_u8; 32];
        let quote_vault_bytes = [5_u8; 32];
        let base_state_bytes = [6_u8; 32];
        let quote_state_bytes = [7_u8; 32];
        let base_lp_bytes = [8_u8; 32];
        let quote_lp_bytes = [10_u8; 32];
        let base_lp_mint_bytes = [11_u8; 32];
        let quote_lp_mint_bytes = [12_u8; 32];
        let base = bs58::encode(base_bytes).into_string();
        let quote = bs58::encode(quote_bytes).into_string();
        let base_vault = bs58::encode(base_vault_bytes).into_string();
        let quote_vault = bs58::encode(quote_vault_bytes).into_string();
        let pool = bs58::encode([13_u8; 32]).into_string();
        let role = |role: &str, bytes: [u8; 32]| ProtocolAccount {
            role: role.to_owned(),
            address: bs58::encode(bytes).into_string(),
            index: None,
        };
        let mut pool_descriptor = descriptor(METEORA_DAMM_V1_PROTOCOL_ID, &pool, &base, &quote);
        pool_descriptor.base_vault = Some(base_vault.clone());
        pool_descriptor.quote_vault = Some(quote_vault.clone());
        pool_descriptor.protocol_accounts = vec![
            role("baseVaultState", base_state_bytes),
            role("quoteVaultState", quote_state_bytes),
            role("baseVaultLp", base_lp_bytes),
            role("quoteVaultLp", quote_lp_bytes),
            role("baseVaultLpMint", base_lp_mint_bytes),
            role("quoteVaultLpMint", quote_lp_mint_bytes),
        ];
        let mut state = MarketState::new(7);
        state
            .replace_watched_pools(vec![WatchedPoolSelection {
                descriptor: pool_descriptor,
                selected_mint: base.clone(),
            }])
            .unwrap();
        state.set_recovery_state(RecoveryState::Live);

        let mut pool_data = vec![0_u8; 952];
        pool_data[..8].copy_from_slice(&[241, 154, 109, 4, 17, 177, 109, 188]);
        pool_data[40..72].copy_from_slice(&base_bytes);
        pool_data[72..104].copy_from_slice(&quote_bytes);
        pool_data[104..136].copy_from_slice(&base_state_bytes);
        pool_data[136..168].copy_from_slice(&quote_state_bytes);
        pool_data[168..200].copy_from_slice(&base_lp_bytes);
        pool_data[200..232].copy_from_slice(&quote_lp_bytes);
        pool_data[233] = 1;

        let dynamic_vault = |mint: [u8; 32], token_vault: [u8; 32], lp_mint: [u8; 32], total| {
            let mut data = vec![0_u8; 1232];
            data[..8].copy_from_slice(&[211, 8, 232, 43, 2, 152, 117, 119]);
            data[8] = 1;
            data[11..19].copy_from_slice(&u64::to_le_bytes(total));
            data[19..51].copy_from_slice(&token_vault);
            data[83..115].copy_from_slice(&mint);
            data[115..147].copy_from_slice(&lp_mint);
            data
        };
        let token = |mint: [u8; 32], amount| {
            let mut data = vec![0_u8; 165];
            data[..32].copy_from_slice(&mint);
            data[64..72].copy_from_slice(&u64::to_le_bytes(amount));
            data
        };
        let mint = |supply| {
            let mut data = vec![0_u8; 82];
            data[36..44].copy_from_slice(&u64::to_le_bytes(supply));
            data[45] = 1;
            data
        };
        let updates = [
            (pool.clone(), meteora_damm_v1::PROGRAM_ID, pool_data),
            (
                bs58::encode(base_state_bytes).into_string(),
                meteora_dynamic_vault::PROGRAM_ID,
                dynamic_vault(base_bytes, base_vault_bytes, base_lp_mint_bytes, 1_000_000),
            ),
            (
                bs58::encode(quote_state_bytes).into_string(),
                meteora_dynamic_vault::PROGRAM_ID,
                dynamic_vault(
                    quote_bytes,
                    quote_vault_bytes,
                    quote_lp_mint_bytes,
                    2_000_000_000,
                ),
            ),
            (
                bs58::encode(base_lp_bytes).into_string(),
                TOKEN_PROGRAM_ID,
                token(base_lp_mint_bytes, 1_000_000),
            ),
            (
                bs58::encode(quote_lp_bytes).into_string(),
                TOKEN_PROGRAM_ID,
                token(quote_lp_mint_bytes, 2_000_000_000),
            ),
            (
                bs58::encode(base_lp_mint_bytes).into_string(),
                TOKEN_PROGRAM_ID,
                mint(1_000_000),
            ),
            (
                bs58::encode(quote_lp_mint_bytes).into_string(),
                TOKEN_PROGRAM_ID,
                mint(2_000_000_000),
            ),
        ];
        for (index, (pubkey, owner, data)) in updates.into_iter().enumerate() {
            let emitted = state
                .apply_account_update(RawAccountUpdate {
                    pubkey,
                    owner_program: owner.to_owned(),
                    data_base64: BASE64.encode(data),
                    slot: 60,
                    write_version: index as u64,
                    commitment: Commitment::Processed,
                    source_id: "fixture".to_owned(),
                    observed_at_unix_ms: 600,
                })
                .unwrap();
            if index < 6 {
                assert!(emitted.is_empty());
            } else {
                assert_eq!(
                    emitted[0].spot_price_quote.as_ref().unwrap().coefficient,
                    "2000000000000000000"
                );
            }
        }
    }

    #[test]
    fn raydium_cpmm_waits_for_vaults_and_emits_effective_reserve_spot_price() {
        let base_bytes = [3_u8; 32];
        let quote_bytes = [9_u8; 32];
        let base_vault_bytes = [4_u8; 32];
        let quote_vault_bytes = [5_u8; 32];
        let base = bs58::encode(base_bytes).into_string();
        let quote = bs58::encode(quote_bytes).into_string();
        let base_vault = bs58::encode(base_vault_bytes).into_string();
        let quote_vault = bs58::encode(quote_vault_bytes).into_string();
        let pool = bs58::encode([8_u8; 32]).into_string();
        let mut pool_descriptor = descriptor(RAYDIUM_CPMM_PROTOCOL_ID, &pool, &base, &quote);
        pool_descriptor.base_vault = Some(base_vault.clone());
        pool_descriptor.quote_vault = Some(quote_vault.clone());
        let mut state = MarketState::new(7);
        state
            .replace_watched_pools(vec![WatchedPoolSelection {
                descriptor: pool_descriptor,
                selected_mint: base.clone(),
            }])
            .unwrap();
        state.set_recovery_state(RecoveryState::Live);

        let mut pool_data = vec![0_u8; raydium_cpmm::POOL_ACCOUNT_LEN];
        pool_data[..8].copy_from_slice(&[247, 237, 227, 245, 215, 195, 222, 70]);
        pool_data[72..104].copy_from_slice(&base_vault_bytes);
        pool_data[104..136].copy_from_slice(&quote_vault_bytes);
        pool_data[168..200].copy_from_slice(&base_bytes);
        pool_data[200..232].copy_from_slice(&quote_bytes);

        let account_update =
            |pubkey: String, owner_program: &str, data: Vec<u8>, write_version| RawAccountUpdate {
                pubkey,
                owner_program: owner_program.to_owned(),
                data_base64: BASE64.encode(data),
                slot: 60,
                write_version,
                commitment: Commitment::Processed,
                source_id: "fixture".to_owned(),
                observed_at_unix_ms: 600,
            };
        let token_account = |mint: [u8; 32], amount: u64| {
            let mut data = vec![0_u8; 165];
            data[..32].copy_from_slice(&mint);
            data[64..72].copy_from_slice(&amount.to_le_bytes());
            data
        };

        assert!(state
            .apply_account_update(account_update(pool, raydium_cpmm::PROGRAM_ID, pool_data, 1,))
            .unwrap()
            .is_empty());
        assert!(state
            .apply_account_update(account_update(
                base_vault,
                TOKEN_PROGRAM_ID,
                token_account(base_bytes, 1_000_000),
                2,
            ))
            .unwrap()
            .is_empty());
        let updates = state
            .apply_account_update(account_update(
                quote_vault,
                TOKEN_PROGRAM_ID,
                token_account(quote_bytes, 2_000_000_000),
                3,
            ))
            .unwrap();
        assert_eq!(
            updates[0].spot_price_quote.as_ref().unwrap().coefficient,
            "2000000000000000000"
        );
    }

    #[test]
    fn replay_duplicate_is_ignored_but_commitment_promotion_is_emitted() {
        let mint_bytes = [3_u8; 32];
        let mint = bs58::encode(mint_bytes).into_string();
        let mut state = MarketState::new(7);
        state
            .replace_watched_pools(vec![WatchedPoolSelection {
                descriptor: descriptor(PUMP_PROTOCOL_ID, "curve", &mint, WRAPPED_SOL_MINT),
                selected_mint: mint,
            }])
            .unwrap();

        let mut event = vec![189, 219, 127, 211, 78, 230, 97, 238];
        event.extend_from_slice(&mint_bytes);
        event.extend_from_slice(&2_000_000_000_u64.to_le_bytes());
        event.extend_from_slice(&1_000_000_u64.to_le_bytes());
        event.push(1);
        let transaction = |commitment| RawTransactionUpdate {
            signature: "signature".to_owned(),
            slot: 20,
            failed: false,
            commitment,
            source_id: "fixture".to_owned(),
            observed_at_unix_ms: 200,
            instructions: vec![RawInstruction {
                program_id: pump::PROGRAM_ID.to_owned(),
                data_base64: BASE64.encode(&event),
                outer_instruction_index: 1,
                inner_instruction_index: Some(2),
                stack_height: Some(2),
                account_addresses: vec![],
            }],
            program_data: vec![],
            token_balances: vec![],
        };

        let processed = state
            .apply_transaction_update(transaction(Commitment::Processed))
            .unwrap();
        assert_eq!(processed.len(), 1);
        assert_eq!(
            processed[0]
                .last_trade_price_quote
                .as_ref()
                .unwrap()
                .coefficient,
            "2000000000000000000"
        );
        assert!(state
            .apply_transaction_update(transaction(Commitment::Processed))
            .unwrap()
            .is_empty());
        let confirmed = state
            .apply_transaction_update(transaction(Commitment::Confirmed))
            .unwrap();
        assert_eq!(confirmed.len(), 1);
        assert_eq!(confirmed[0].commitment, Commitment::Confirmed);
    }

    #[test]
    fn snapshot_reset_clears_fork_sensitive_trade_and_dedup_state() {
        let mint_bytes = [3_u8; 32];
        let mint = bs58::encode(mint_bytes).into_string();
        let mut state = MarketState::new(7);
        state
            .replace_watched_pools(vec![WatchedPoolSelection {
                descriptor: descriptor(PUMP_PROTOCOL_ID, "curve", &mint, WRAPPED_SOL_MINT),
                selected_mint: mint,
            }])
            .unwrap();

        let mut event = vec![189, 219, 127, 211, 78, 230, 97, 238];
        event.extend_from_slice(&mint_bytes);
        event.extend_from_slice(&2_000_000_000_u64.to_le_bytes());
        event.extend_from_slice(&1_000_000_u64.to_le_bytes());
        event.push(1);
        let transaction = RawTransactionUpdate {
            signature: "forked-signature".to_owned(),
            slot: 20,
            failed: false,
            commitment: Commitment::Processed,
            source_id: "fixture".to_owned(),
            observed_at_unix_ms: 200,
            instructions: vec![RawInstruction {
                program_id: pump::PROGRAM_ID.to_owned(),
                data_base64: BASE64.encode(&event),
                outer_instruction_index: 1,
                inner_instruction_index: Some(2),
                stack_height: Some(2),
                account_addresses: vec![],
            }],
            program_data: vec![],
            token_balances: vec![],
        };

        let first = state.apply_transaction_update(transaction.clone()).unwrap();
        assert_eq!(first.len(), 1);
        assert!(state.snapshots()[0].last_trade_price_quote.is_some());
        let first_epoch = first[0].stream_epoch;

        state.reset_for_snapshot();
        let reset = state.snapshots();
        assert!(reset[0].last_trade_price_quote.is_none());
        assert!(reset[0].stream_epoch > first_epoch);
        assert_eq!(
            state.apply_transaction_update(transaction).unwrap().len(),
            1
        );
    }
}
