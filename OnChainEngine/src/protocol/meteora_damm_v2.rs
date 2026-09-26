use thiserror::Error;

pub const PROGRAM_ID: &str = "cpamdpZCGKUy5JxQXB4dcpGPiikHawvSWAd6mEn1sGG";
pub const POOL_ACCOUNT_LEN: usize = 1112;
const POOL_DISCRIMINATOR: [u8; 8] = [241, 154, 109, 4, 17, 177, 109, 188];
const SWAP_DISCRIMINATOR: [u8; 8] = [248, 198, 158, 145, 225, 117, 135, 200];
const SWAP_2_DISCRIMINATOR: [u8; 8] = [65, 75, 63, 76, 235, 91, 91, 136];

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MeteoraDammV2PoolState {
    pub token_a_mint: String,
    pub token_b_mint: String,
    pub token_a_vault: String,
    pub token_b_vault: String,
    pub sqrt_price_x64: u128,
    pub pool_status: u8,
    pub collect_fee_mode: u8,
    pub layout_version: u8,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum MeteoraDammV2DecodeError {
    #[error("account owner is not the Meteora DAMM v2 program")]
    WrongOwner,
    #[error("account length is not the supported Meteora DAMM v2 Pool layout")]
    WrongLength,
    #[error("account discriminator is not a Meteora DAMM v2 Pool")]
    WrongDiscriminator,
    #[error("Meteora DAMM v2 pool status is invalid")]
    InvalidStatus,
    #[error("Meteora DAMM v2 collect-fee mode is invalid")]
    InvalidCollectFeeMode,
    #[error("Meteora DAMM v2 sqrt price is zero")]
    ZeroSqrtPrice,
}

pub fn is_swap_instruction(data: &[u8]) -> bool {
    data.starts_with(&SWAP_DISCRIMINATOR) || data.starts_with(&SWAP_2_DISCRIMINATOR)
}

pub fn decode_pool(
    owner_program: &str,
    data: &[u8],
) -> Result<MeteoraDammV2PoolState, MeteoraDammV2DecodeError> {
    if owner_program != PROGRAM_ID {
        return Err(MeteoraDammV2DecodeError::WrongOwner);
    }
    if data.len() != POOL_ACCOUNT_LEN {
        return Err(MeteoraDammV2DecodeError::WrongLength);
    }
    if data[..8] != POOL_DISCRIMINATOR {
        return Err(MeteoraDammV2DecodeError::WrongDiscriminator);
    }
    let pool_status = data[481];
    if pool_status > 1 {
        return Err(MeteoraDammV2DecodeError::InvalidStatus);
    }
    let collect_fee_mode = data[484];
    if collect_fee_mode > 2 {
        return Err(MeteoraDammV2DecodeError::InvalidCollectFeeMode);
    }
    let sqrt_price_x64 = u128_at(data, 456);
    if sqrt_price_x64 == 0 {
        return Err(MeteoraDammV2DecodeError::ZeroSqrtPrice);
    }

    Ok(MeteoraDammV2PoolState {
        token_a_mint: pubkey(data, 168),
        token_b_mint: pubkey(data, 200),
        token_a_vault: pubkey(data, 232),
        token_b_vault: pubkey(data, 264),
        sqrt_price_x64,
        pool_status,
        collect_fee_mode,
        layout_version: data[696],
    })
}

fn pubkey(data: &[u8], offset: usize) -> String {
    bs58::encode(&data[offset..offset + 32]).into_string()
}

fn u128_at(data: &[u8], offset: usize) -> u128 {
    u128::from_le_bytes(
        data[offset..offset + 16]
            .try_into()
            .expect("validated layout"),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decodes_current_pool_layout_and_swap_variants() {
        let mut data = vec![0_u8; POOL_ACCOUNT_LEN];
        data[..8].copy_from_slice(&POOL_DISCRIMINATOR);
        data[168..200].copy_from_slice(&[1; 32]);
        data[200..232].copy_from_slice(&[2; 32]);
        data[232..264].copy_from_slice(&[3; 32]);
        data[264..296].copy_from_slice(&[4; 32]);
        data[456..472].copy_from_slice(&(1_u128 << 64).to_le_bytes());
        data[481] = 0;
        data[484] = 2;
        data[696] = 1;

        let state = decode_pool(PROGRAM_ID, &data).unwrap();
        assert_eq!(state.token_a_mint, bs58::encode([1_u8; 32]).into_string());
        assert_eq!(state.token_b_vault, bs58::encode([4_u8; 32]).into_string());
        assert_eq!(state.sqrt_price_x64, 1_u128 << 64);
        assert_eq!(state.collect_fee_mode, 2);
        assert_eq!(state.layout_version, 1);
        assert!(is_swap_instruction(&SWAP_DISCRIMINATOR));
        assert!(is_swap_instruction(&SWAP_2_DISCRIMINATOR));
        assert!(!is_swap_instruction(&[1; 8]));
    }

    #[test]
    fn rejects_wrong_owner_length_discriminator_and_state() {
        let mut data = vec![0_u8; POOL_ACCOUNT_LEN];
        data[..8].copy_from_slice(&POOL_DISCRIMINATOR);
        data[456..472].copy_from_slice(&(1_u128 << 64).to_le_bytes());
        assert_eq!(
            decode_pool("wrong", &data),
            Err(MeteoraDammV2DecodeError::WrongOwner)
        );
        assert_eq!(
            decode_pool(PROGRAM_ID, &data[..POOL_ACCOUNT_LEN - 1]),
            Err(MeteoraDammV2DecodeError::WrongLength)
        );
        data[0] = 0;
        assert_eq!(
            decode_pool(PROGRAM_ID, &data),
            Err(MeteoraDammV2DecodeError::WrongDiscriminator)
        );
        data[..8].copy_from_slice(&POOL_DISCRIMINATOR);
        data[481] = 2;
        assert_eq!(
            decode_pool(PROGRAM_ID, &data),
            Err(MeteoraDammV2DecodeError::InvalidStatus)
        );
        data[481] = 0;
        data[484] = 3;
        assert_eq!(
            decode_pool(PROGRAM_ID, &data),
            Err(MeteoraDammV2DecodeError::InvalidCollectFeeMode)
        );
        data[484] = 0;
        data[456..472].fill(0);
        assert_eq!(
            decode_pool(PROGRAM_ID, &data),
            Err(MeteoraDammV2DecodeError::ZeroSqrtPrice)
        );
    }
}
