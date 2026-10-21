import unittest
from unittest.mock import Mock, patch
import capacity


class CapacityTests(unittest.TestCase):
    def test_kvm2_rejected_even_without_current_pressure(self):
        with self.assertRaisesRegex(ValueError, '10112'):
            capacity.validate_memory('MemTotal: 7812500 kB\nMemAvailable: 7000000 kB\n')

    def test_boundary_and_available_margin(self):
        total = 10112 * 1024
        capacity.validate_memory(f'MemTotal: {total} kB\nMemAvailable: 1572864 kB\n')
        for text in (f'MemTotal: {total-1} kB\nMemAvailable: 2000000 kB\n',
                     f'MemTotal: {total} kB\nMemAvailable: 1572863 kB\n',
                     '', 'MemTotal: -1 kB\n', 'MemTotal: 12000000 MB\n'):
            with self.assertRaises(ValueError):
                capacity.validate_memory(text)

    def test_runtime_requires_bounded_media_slice(self):
        runner = Mock(return_value='1073741824\n')
        capacity.validate_media_slice(runner)
        for value in ('infinity\n', '0\n', '2147483648\n', ''):
            runner.return_value = value
            with self.assertRaises(ValueError):
                capacity.validate_media_slice(runner)

    def test_preflight_checks_capacity_before_credentials(self):
        import preflight
        with patch.object(preflight, 'check_capacity', side_effect=ValueError('capacity denied')) as check:
            with self.assertRaisesRegex(ValueError, 'capacity denied'):
                preflight.main()
            check.assert_called_once()
