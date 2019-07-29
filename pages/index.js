import Layout from '../components/Layout';
import Link from 'next/link';
import dataService from '../services/dataservice';

const Index = props => (
  <Layout>
    <h1>Active Clients</h1>
    <ul>
      {props.leases.map(lease => (
        <li key={lease.mac}>
          <Link href="/device/[id]" as={`/device/${lease.mac}`}>
            <a>{lease.ip} - {lease.end}</a>
          </Link>
        </li>
      ))}
    </ul>
  </Layout>
);

Index.getInitialProps = async function() {
    const data = await dataService.getActiveLeases();
    console.log(`Show data fetched. Count: ${data.length}`);

    return {
        leases: data
    };
};

export default Index;