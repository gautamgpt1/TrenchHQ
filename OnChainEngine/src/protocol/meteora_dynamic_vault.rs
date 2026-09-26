use thiserror::Error;

pub const PROGRAM_ID: &str = "24Uqj9JCLxUeoC3hGfh5W3s9FM9uCHDS2SG3LYwBpyTi";
pub const LOCKED_PROFIT_DEGRADATION_DENOMINATOR: u128 = 1_000_000_000_000;
const MINIMUM_VAULT_ACCOUNT_LEN: usize = 1227;
const MAXIMUM_VAULT_ACCOUNT_LEN: usize = 10240;
const VAULT_DISCRIMINATOR: [u8; 8] = [211, 8, 232, 43, 2, 152, 117, 119];

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MeteoraDynamicVaultState {
    pub enabled: bool,
    pub total_amount: u64,
    pub token_vault: String,
    pub token_mint: String,
    pub lp_mint: String,
    pub last_updated_locked_profit: u64,
    pub last_report: u64,
    pub locked_profit_degradation: u64,
}

impl MeteoraDynamicVaultState {
    pub fn unlocked_amount(&self, current_time: u64) -> Option<u64> {
        let duration = current_time.checked_sub(self.last_report)? as u128;
        let ratio = duration.checked_mul(self.locked_profit_degradation as u128)?;
        let locked_profit = if ratio > LOCKED_PROFIT_DEGRADATION_DENOMINATOR {
            0
        } else {
            (self.last_updated_locked_profit as u128)
                .checked_mul(LOCKED_PROFIT_DEGRADATION_DENOMINATOR - ratio)?
                .checked_div(LOCKED_PROFIT_DEGRADATION_DENOMINATOR)? as u64
        };
        self.total_amount.checked_sub(locked_profit)
    }
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum MeteoraDynamicVaultDecodeError {
    #[error("account owner is not the Meteora Dynamic Vault program")]
    WrongOwner,
    #[error("account length is not a supported Meteora Dynamic Vault allocation")]
    WrongLength,
    #[error("account discriminator is not a Meteora Dynamic Vault")]
    WrongDiscriminator,
    #[error("Meteora Dynamic Vault enabled flag is invalid")]
    InvalidEnabledFlag,
}

pub fn decode_vault(
    owner_program: &str,
    data: &[u8],
) -> Result<MeteoraDynamicVaultState, MeteoraDynamicVaultDecodeError> {
    if owner_program != PROGRAM_ID {
        return Err(MeteoraDynamicVaultDecodeError::WrongOwner);
    }
    if !(MINIMUM_VAULT_ACCOUNT_LEN..=MAXIMUM_VAULT_ACCOUNT_LEN).contains(&data.len()) {
        return Err(MeteoraDynamicVaultDecodeError::WrongLength);
    }
    if data[..8] != VAULT_DISCRIMINATOR {
        return Err(MeteoraDynamicVaultDecodeError::WrongDiscriminator);
    }
    let enabled = match data[8] {
        0 => false,
        1 => true,
        _ => return Err(MeteoraDynamicVaultDecodeError::InvalidEnabledFlag),
    };
    Ok(MeteoraDynamicVaultState {
        enabled,
        total_amount: u64_at(data, 11),
        token_vault: pubkey(data, 19),
        token_mint: pubkey(data, 83),
        lp_mint: pubkey(data, 115),
        last_updated_locked_profit: u64_at(data, 1203),
        last_report: u64_at(data, 1211),
        locked_profit_degradation: u64_at(data, 1219),
    })
}

fn pubkey(data: &[u8], offset: usize) -> String {
    bs58::encode(&data[offset..offset + 32]).into_string()
}

fn u64_at(data: &[u8], offset: usize) -> u64 {
    u64::from_le_bytes(
        data[offset..offset + 8]
            .try_into()
            .expect("validated layout"),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decodes_vault_and_applies_locked_profit_decay() {
        let mut data = vec![0_u8; 1232];
        data[..8].copy_from_slice(&VAULT_DISCRIMINATOR);
        data[8] = 1;
        data[11..19].copy_from_slice(&1_000_u64.to_le_bytes());
        data[19..51].copy_from_slice(&[1; 32]);
        data[83..115].copy_from_slice(&[2; 32]);
        data[115..147].copy_from_slice(&[3; 32]);
        data[1203..1211].copy_from_slice(&500_u64.to_le_bytes());
        data[1211..1219].copy_from_slice(&100_u64.to_le_bytes());
        data[1219..1227].copy_from_slice(&500_000_000_000_u64.to_le_bytes());

        let state = decode_vault(PROGRAM_ID, &data).unwrap();
        assert!(state.enabled);
        assert_eq!(state.unlocked_amount(101), Some(750));
        assert_eq!(state.unlocked_amount(103), Some(1_000));
        assert_eq!(state.unlocked_amount(99), None);
    }
}
